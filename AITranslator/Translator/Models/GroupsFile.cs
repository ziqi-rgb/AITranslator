using System;
using System.Collections.Generic;

namespace AITranslator.Translator.Models
{
    /// <summary>
    /// 单个分组的序列化元数据
    /// </summary>
    public class GroupMeta
    {
        /// <summary>
        /// 分组唯一标识（GUID）
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();
        /// <summary>
        /// 分组显示名称
        /// </summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// 父分组 ID，null 表示根级分组
        /// </summary>
        public string? ParentId { get; set; }
        /// <summary>
        /// 属于此分组的任务 DicName 列表
        /// </summary>
        public List<string> TaskDicNames { get; set; } = new();
        /// <summary>
        /// 是否为导入时按目录结构自动创建的分组
        /// </summary>
        public bool IsAutoCreated { get; set; }
    }

    /// <summary>
    /// 分组文件的顶层结构，序列化到 翻译数据/分组.json
    /// </summary>
    public class GroupsFile
    {
        /// <summary>
        /// 所有分组（扁平列表，通过 ParentId 建立层级）
        /// </summary>
        public List<GroupMeta> Groups { get; set; } = new();
        /// <summary>
        /// 未归入任何分组的任务 DicName 列表
        /// </summary>
        public List<string> UngroupedTaskDicNames { get; set; } = new();
    }
}
