using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using System.Collections.ObjectModel;
using System.IO;

namespace AITranslator.View.Models
{
    /// <summary>
    /// 分组数据的持久化与内存树构建
    /// </summary>
    public static class GroupStore
    {
        /// <summary>
        /// 分组元数据文件路径
        /// </summary>
        const string GroupsPath = $"{PublicParams.TranslatedDataDic}/分组.json";

        /// <summary>
        /// 从磁盘加载分组元数据。文件不存在时返回空结构。
        /// </summary>
        public static GroupsFile Load()
        {
            if (!File.Exists(GroupsPath))
                return new GroupsFile();

            try
            {
                return JsonPersister.Load<GroupsFile>(GroupsPath);
            }
            catch
            {
                // 文件损坏时回退为空结构，避免阻塞启动
                return new GroupsFile();
            }
        }

        /// <summary>
        /// 将分组元数据保存到磁盘
        /// </summary>
        public static void Save(GroupsFile groups)
        {
            if (!Directory.Exists(PublicParams.TranslatedDataDic))
                Directory.CreateDirectory(PublicParams.TranslatedDataDic);
            JsonPersister.Save(groups, GroupsPath);
        }

        /// <summary>
        /// 从 GroupsFile 构建内存中的 TaskGroup 树。
        /// 返回根级分组列表（不含"未分组"节点，由调用方单独创建）。
        /// </summary>
        public static List<TaskGroup> BuildTree(GroupsFile groups, Dictionary<string, TranslationTask> taskMap)
        {
            // 先创建所有 TaskGroup 对象
            var groupMap = new Dictionary<string, TaskGroup>();
            foreach (var meta in groups.Groups)
            {
                var tg = new TaskGroup { Id = meta.Id, Name = meta.Name, IsAutoCreated = meta.IsAutoCreated };
                groupMap[meta.Id] = tg;
            }

            // 建立父子关系（使用 AddChild 以维护 Parent 引用）
            foreach (var meta in groups.Groups)
            {
                if (meta.ParentId != null && groupMap.TryGetValue(meta.ParentId, out var parent))
                    parent.AddChild(groupMap[meta.Id]);
            }

            // 挂载任务（使用 AddTask 以维护去重）
            foreach (var meta in groups.Groups)
            {
                var tg = groupMap[meta.Id];
                foreach (var dicName in meta.TaskDicNames)
                {
                    if (taskMap.TryGetValue(dicName, out var task))
                        tg.AddTask(task);
                }
            }

            // 收集根级分组（ParentId 为 null）
            var roots = new List<TaskGroup>();
            foreach (var meta in groups.Groups)
            {
                if (meta.ParentId == null)
                    roots.Add(groupMap[meta.Id]);
            }
            return roots;
        }

        /// <summary>
        /// 将内存中的 TaskGroup 树 + 未分组任务序列化为 GroupsFile
        /// </summary>
        public static GroupsFile ToGroupsFile(ObservableCollection<TaskGroup> rootGroups, TaskGroup ungrouped)
        {
            var groups = new GroupsFile();

            // 收集所有分组（DFS）
            void Collect(TaskGroup tg)
            {
                if (tg.IsUngrouped) return; // "未分组"节点不序列化
                var meta = new GroupMeta
                {
                    Id = tg.Id,
                    Name = tg.Name,
                    ParentId = (tg.Parent != null && !tg.Parent.IsUngrouped) ? tg.Parent.Id : null,
                    TaskDicNames = tg.Tasks.Select(t => t.DicName).ToList(),
                    IsAutoCreated = tg.IsAutoCreated
                };
                groups.Groups.Add(meta);
                foreach (var child in tg.Children)
                    Collect(child);
            }

            // 根级分组（排除"未分组"）
            foreach (var root in rootGroups)
                if (!root.IsUngrouped)
                    Collect(root);

            // "未分组"节点下的任务
            groups.UngroupedTaskDicNames = ungrouped.Tasks.Select(t => t.DicName).ToList();

            return groups;
        }
    }
}
