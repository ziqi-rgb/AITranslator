using AITranslator.Translator.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Threading;

namespace AITranslator.View.Models
{
    /// <summary>
    /// ViewModel 的分组管理扩展（partial class）
    /// </summary>
    public partial class ViewModel
    {
        #region 分组属性

        /// <summary>
        /// 根级分组列表（不含"未分组"节点，供逻辑遍历使用）
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TaskGroup> rootGroups = new();

        /// <summary>
        /// "未分组"固定节点
        /// </summary>
        public TaskGroup UngroupedGroup { get; } = new() { Name = "未分组", IsUngrouped = true };

        /// <summary>
        /// TreeView 顶层绑定集合：[未分组, 根分组1, 根分组2, ...]
        /// </summary>
        public ObservableCollection<object> TreeRootNodes { get; } = new();

        /// <summary>
        /// 未完成视图当前选中的分组（null = 显示全部任务）
        /// </summary>
        [ObservableProperty]
        private TaskGroup? selectedGroup;

        /// <summary>
        /// 已完成视图当前选中的分组（null = 显示全部任务）
        /// </summary>
        [ObservableProperty]
        private TaskGroup? selectedCompletedGroup;

        /// <summary>
        /// 过滤后的未完成任务列表（UI 绑定用）
        /// SelectedGroup == null 时等同于 UnfinishedTasks
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TranslationTask> filteredUnfinishedTasks = new();

        /// <summary>
        /// 过滤后的已完成任务列表（UI 绑定用）
        /// SelectedCompletedGroup == null 时等同于 CompletedTasks
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TranslationTask> filteredCompletedTasks = new();

        #endregion

        #region 分组过滤

        partial void OnSelectedGroupChanged(TaskGroup? value)
        {
            RebuildFilteredUnfinished();
        }

        partial void OnSelectedCompletedGroupChanged(TaskGroup? value)
        {
            RebuildFilteredCompleted();
        }

        /// <summary>
        /// 重建未完成过滤列表（切换选中分组等低频操作，全量按序重建）
        /// </summary>
        public void RebuildFilteredUnfinished()
        {
            List<TranslationTask> target;
            if (SelectedGroup == null)
            {
                target = new List<TranslationTask>(UnfinishedTasks);
            }
            else
            {
                // 只显示该分组（含子分组）的任务：先建 HashSet，O(N) 判定
                var groupSet = SelectedGroup.GetTaskSet();
                target = UnfinishedTasks.Where(t => groupSet.Contains(t)).ToList();
            }

            FilteredUnfinishedTasks.Clear();
            foreach (var t in target)
                FilteredUnfinishedTasks.Add(t);
        }

        /// <summary>
        /// 重建已完成过滤列表（切换选中分组等低频操作，全量按序重建）
        /// </summary>
        public void RebuildFilteredCompleted()
        {
            List<TranslationTask> target;
            if (SelectedCompletedGroup == null)
            {
                target = new List<TranslationTask>(CompletedTasks);
            }
            else
            {
                var groupSet = SelectedCompletedGroup.GetTaskSet();
                target = CompletedTasks.Where(t => groupSet.Contains(t)).ToList();
            }

            FilteredCompletedTasks.Clear();
            foreach (var t in target)
                FilteredCompletedTasks.Add(t);
        }

        /// <summary>
        /// 重建未完成 + 已完成两个过滤列表（切换选中分组、批量操作后使用）
        /// </summary>
        public void RebuildFilteredTasks()
        {
            RebuildFilteredUnfinished();
            RebuildFilteredCompleted();
        }

        /// <summary>
        /// 任务从未完成变为已完成时的增量刷新（避免全列表重建）
        /// </summary>
        public void OnTaskCompleted(TranslationTask task)
        {
            if (FilteredUnfinishedTasks.Contains(task))
                FilteredUnfinishedTasks.Remove(task);

            if (SelectedCompletedGroup == null || SelectedCompletedGroup.GetTaskSet().Contains(task))
            {
                if (!FilteredCompletedTasks.Contains(task))
                    FilteredCompletedTasks.Add(task);
            }
        }

        /// <summary>
        /// 任务被删除时的增量刷新（避免全列表重建）
        /// </summary>
        public void OnTaskRemoved(TranslationTask task)
        {
            FilteredUnfinishedTasks.Remove(task);
            FilteredCompletedTasks.Remove(task);
        }

        /// <summary>
        /// 任务在分组间移动时的增量刷新（避免全列表重建）
        /// </summary>
        public void OnTaskGroupChanged(TranslationTask task)
        {
            if (task.State == TaskState.Completed)
            {
                bool shouldShow = SelectedCompletedGroup == null || SelectedCompletedGroup.GetTaskSet().Contains(task);
                if (shouldShow && !FilteredCompletedTasks.Contains(task))
                    FilteredCompletedTasks.Add(task);
                else if (!shouldShow)
                    FilteredCompletedTasks.Remove(task);
            }
            else
            {
                bool shouldShow = SelectedGroup == null || SelectedGroup.GetTaskSet().Contains(task);
                if (shouldShow && !FilteredUnfinishedTasks.Contains(task))
                    FilteredUnfinishedTasks.Add(task);
                else if (!shouldShow)
                    FilteredUnfinishedTasks.Remove(task);
            }
        }

        #endregion

        #region 加载 / 保存

        /// <summary>
        /// 在所有任务加载完毕后调用，从磁盘构建分组树并将任务分配到对应分组。
        /// 未在分组.json 中出现的任务自动归入"未分组"。
        /// </summary>
        public void LoadGroups()
        {
            var groupsFile = GroupStore.Load();

            // 建立 DicName → Task 的索引（包含未完成 + 已完成）
            var taskMap = new Dictionary<string, TranslationTask>();
            foreach (var t in UnfinishedTasks)
                taskMap[t.DicName] = t;
            foreach (var t in CompletedTasks)
                taskMap[t.DicName] = t;

            // 构建分组树（GroupStore.BuildTree 内部使用 AddChild/AddTask）
            var roots = GroupStore.BuildTree(groupsFile, taskMap);

            RootGroups.Clear();
            foreach (var root in roots)
                RootGroups.Add(root);

            // 将未在任何分组中的任务放入"未分组"
            UngroupedGroup.Tasks.Clear();

            var assignedDicNames = new HashSet<string>();
            void MarkAssigned(TaskGroup tg)
            {
                foreach (var t in tg.Tasks)
                    assignedDicNames.Add(t.DicName);
                foreach (var c in tg.Children)
                    MarkAssigned(c);
            }
            foreach (var root in RootGroups)
                MarkAssigned(root);

            // 从分组.json 中读取"未分组"任务
            foreach (var dicName in groupsFile.UngroupedTaskDicNames)
            {
                if (taskMap.TryGetValue(dicName, out var task))
                    UngroupedGroup.AddTask(task);
            }

            // 兜底：分组.json 中完全没提到的任务也归入"未分组"
            foreach (var kvp in taskMap)
            {
                if (!assignedDicNames.Contains(kvp.Key) && !UngroupedGroup.Tasks.Contains(kvp.Value))
                    UngroupedGroup.AddTask(kvp.Value);
            }

            // 重建 TreeRootNodes
            RebuildTreeRootNodes();

            // 初始化过滤列表（默认显示全部）
            RebuildFilteredTasks();
        }

        /// <summary>
        /// 分组写盘防抖：连续多次 SaveGroups（如批量导入、逐个删除）在窗口期内合并为一次写盘。
        /// </summary>
        static readonly object _groupsSaveLock = new();
        static Timer? _groupsSaveTimer;
        const int GroupsSaveDebounceMs = 1000;

        /// <summary>
        /// 将当前分组树保存到磁盘（防抖：~1s 内的多次调用合并为一次写盘）
        /// </summary>
        public void SaveGroups()
        {
            lock (_groupsSaveLock)
            {
                _groupsSaveTimer?.Dispose();
                _groupsSaveTimer = new Timer(_ => FlushGroups(), null, GroupsSaveDebounceMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 立即将分组树写入磁盘（关键路径：应用关闭、批量删除收尾等需要数据落盘时使用）。
        /// 序列化必须在 UI 线程执行（分组集合与 UI 绑定共享），计时器回调时自动切回。
        /// </summary>
        public void FlushGroups()
        {
            lock (_groupsSaveLock)
            {
                _groupsSaveTimer?.Dispose();
                _groupsSaveTimer = null;
            }

            void DoSave()
            {
                var groupsFile = GroupStore.ToGroupsFile(RootGroups, UngroupedGroup);
                GroupStore.Save(groupsFile);
            }

            if (Dispatcher != null && !Dispatcher.CheckAccess())
                Dispatcher.Invoke(DoSave);
            else
                DoSave();
        }

        #endregion

        #region 分组 CRUD

        /// <summary>
        /// 创建新分组。parent 为 null 时创建根级分组，否则作为 parent 的子分组。
        /// </summary>
        public TaskGroup CreateGroup(string name, TaskGroup? parent = null)
        {
            var group = new TaskGroup { Name = name };

            if (parent != null)
            {
                parent.AddChild(group);
            }
            else
            {
                RootGroups.Add(group);
                TreeRootNodes.Add(group);
            }

            SaveGroups();
            return group;
        }

        /// <summary>
        /// 重命名分组
        /// </summary>
        public void RenameGroup(TaskGroup group, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return;
            group.Name = newName.Trim();
            SaveGroups();
        }

        /// <summary>
        /// 删除分组。组内所有任务（含子分组）回落到"未分组"。
        /// </summary>
        public void DeleteGroup(TaskGroup group)
        {
            // 收集此分组及所有子孙的任务
            var tasksToMove = group.CollectAllTasks();

            // 将任务移到"未分组"
            foreach (var task in tasksToMove)
                UngroupedGroup.AddTask(task);

            // 从父级移除
            if (group.Parent != null)
                group.Parent.RemoveChild(group);
            else
            {
                RootGroups.Remove(group);
                TreeRootNodes.Remove(group);
            }

            SaveGroups();
        }

        /// <summary>
        /// 删除分组及其所有子分组下的全部任务（含磁盘数据），不可恢复。
        /// </summary>
        public async Task DeleteGroupWithContents(TaskGroup group)
        {
            // 收集此分组及所有子孙的任务，逐个彻底删除（含磁盘目录）
            var tasksToDelete = group.CollectAllTasks();
            foreach (var task in tasksToDelete)
                await RemoveTask(task, showErrorMsg: false, askDeleteEmptyGroup: false, skipSaveGroups: true);

            // 从父级移除分组本身
            if (group.Parent != null)
                group.Parent.RemoveChild(group);
            else
            {
                RootGroups.Remove(group);
                TreeRootNodes.Remove(group);
            }

            // 若被删分组正是当前选中项，回退到"显示全部"
            if (SelectedGroup != null && IsSelfOrDescendant(SelectedGroup, group))
                SelectedGroup = null;
            if (SelectedCompletedGroup != null && IsSelfOrDescendant(SelectedCompletedGroup, group))
                SelectedCompletedGroup = null;

            // 批量删除收尾：统一落盘一次（替代逐次 SaveGroups）
            FlushGroups();
            RebuildFilteredTasks();
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]已删除分组[{group.Name}]及其全部内容");
        }

        /// <summary>
        /// 判断 node 是否为 ancestor 本身或其子孙
        /// </summary>
        static bool IsSelfOrDescendant(TaskGroup node, TaskGroup ancestor)
        {
            for (var cur = node; cur != null; cur = cur.Parent)
                if (cur == ancestor)
                    return true;
            return false;
        }

        /// <summary>
        /// 将任务从一个分组移动到另一个分组
        /// </summary>
        public void MoveTaskToGroup(TranslationTask task, TaskGroup targetGroup)
        {
            RemoveTaskFromAllGroups(task);
            targetGroup.AddTask(task);
            SaveGroups();

            // 增量刷新过滤列表（避免全列表重建）
            OnTaskGroupChanged(task);
        }

        /// <summary>
        /// 将新任务添加到指定分组（默认"未分组"）
        /// </summary>
        public void AddTaskToGroup(TranslationTask task, TaskGroup? group = null)
        {
            (group ?? UngroupedGroup).AddTask(task);
        }

        #endregion

        #region 内部辅助

        /// <summary>
        /// 从所有分组中移除指定任务（不修改扁平集合）
        /// </summary>
        void RemoveTaskFromAllGroups(TranslationTask task)
        {
            UngroupedGroup.RemoveTask(task);
            void RemoveRecursively(TaskGroup tg)
            {
                tg.RemoveTask(task);
                foreach (var c in tg.Children)
                    RemoveRecursively(c);
            }
            foreach (var root in RootGroups)
                RemoveRecursively(root);
        }

        /// <summary>
        /// 查找包含指定任务的分组
        /// </summary>
        public TaskGroup? FindGroupOfTask(TranslationTask task)
        {
            if (UngroupedGroup.Tasks.Contains(task))
                return UngroupedGroup;

            TaskGroup? Search(TaskGroup tg)
            {
                if (tg.Tasks.Contains(task))
                    return tg;
                foreach (var c in tg.Children)
                {
                    var found = Search(c);
                    if (found != null) return found;
                }
                return null;
            }

            foreach (var root in RootGroups)
            {
                var found = Search(root);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 重建 TreeRootNodes（"未分组" + 所有根级分组）
        /// </summary>
        void RebuildTreeRootNodes()
        {
            TreeRootNodes.Clear();
            TreeRootNodes.Add(UngroupedGroup);
            foreach (var root in RootGroups)
                TreeRootNodes.Add(root);
        }

        #endregion
    }
}
