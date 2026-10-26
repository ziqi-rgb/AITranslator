using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AITranslator.View.Models
{
    /// <summary>
    /// 翻译任务分组节点，用于分组树展示的多级分组
    /// </summary>
    public partial class TaskGroup : ObservableObject
    {
        [ObservableProperty]
        private string id;

        [ObservableProperty]
        private string name;

        /// <summary>
        /// 父分组，null 表示根级
        /// </summary>
        public TaskGroup? Parent { get; set; }

        /// <summary>
        /// 子分组集合（类型安全访问）
        /// </summary>
        public ObservableCollection<TaskGroup> Children { get; } = new();

        /// <summary>
        /// 属于此分组的翻译任务（类型安全访问）
        /// </summary>
        public ObservableCollection<TranslationTask> Tasks { get; } = new();

        /// <summary>
        /// 是否为根级固定的"未分组"节点
        /// </summary>
        public bool IsUngrouped { get; set; }

        /// <summary>
        /// 是否为导入时按目录结构自动创建的分组（手动新建的为 false）
        /// </summary>
        public bool IsAutoCreated { get; set; }

        public TaskGroup()
        {
            Id = System.Guid.NewGuid().ToString();
            Name = "";
        }

        #region 任务 / 子分组管理

        /// <summary>
        /// 添加任务到分组
        /// </summary>
        public void AddTask(TranslationTask task)
        {
            if (Tasks.Contains(task)) return;
            Tasks.Add(task);
        }

        /// <summary>
        /// 从分组移除任务
        /// </summary>
        public void RemoveTask(TranslationTask task)
        {
            Tasks.Remove(task);
        }

        /// <summary>
        /// 添加子分组
        /// </summary>
        public void AddChild(TaskGroup child)
        {
            if (Children.Contains(child)) return;
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>
        /// 移除子分组（含其所有子孙）
        /// </summary>
        public void RemoveChild(TaskGroup child)
        {
            Children.Remove(child);
        }

        #endregion

        /// <summary>
        /// 递归统计此分组及所有子分组的任务总数
        /// </summary>
        public int CountAllTasks()
        {
            int count = Tasks.Count;
            foreach (var child in Children)
                count += child.CountAllTasks();
            return count;
        }

        /// <summary>
        /// 生成完整的层级路径，如 "A/B/C"
        /// </summary>
        public string GetFullPath()
        {
            if (Parent is null)
                return Name;
            return Parent.GetFullPath() + "/" + Name;
        }

        /// <summary>
        /// 递归收集此分组及所有子孙分组的任务
        /// </summary>
        public List<TranslationTask> CollectAllTasks()
        {
            var result = new List<TranslationTask>(Tasks);
            foreach (var child in Children)
                result.AddRange(child.CollectAllTasks());
            return result;
        }

        /// <summary>
        /// 获取此分组及所有子孙分组的任务集合（用于 O(1) 过滤判定）
        /// </summary>
        public HashSet<TranslationTask> GetTaskSet()
        {
            var set = new HashSet<TranslationTask>(Tasks);
            foreach (var child in Children)
                set.UnionWith(child.GetTaskSet());
            return set;
        }
    }
}
