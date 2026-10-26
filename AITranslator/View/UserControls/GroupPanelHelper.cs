using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AITranslator.View.Models;
using AITranslator.View.Windows;

namespace AITranslator.View.UserControls
{
    /// <summary>
    /// 分组树面板的共享交互逻辑（未完成视图 / 已完成视图复用）
    /// </summary>
    internal static class GroupPanelHelper
    {
        /// <summary>
        /// 找到指定坐标位置下的 TreeViewItem
        /// </summary>
        public static TreeViewItem? FindItemAt(TreeView treeView, Point position)
        {
            var element = treeView.InputHitTest(position) as DependencyObject;
            while (element != null && element is not TreeViewItem)
                element = VisualTreeHelper.GetParent(element);
            return element as TreeViewItem;
        }

        /// <summary>
        /// 构建分组右键菜单（新建子分组 / 重命名 / 导出翻译结果 / 删除分组）
        /// </summary>
        public static ContextMenu BuildContextMenu(TaskGroup group)
        {
            var menu = new ContextMenu();

            var miNew = new MenuItem { Header = "新建子分组" };
            miNew.Click += (_, _) =>
            {
                string? name = Microsoft.VisualBasic.Interaction.InputBox("请输入子分组名称：", $"在「{group.Name}」下新建");
                if (string.IsNullOrWhiteSpace(name)) return;
                ViewModelManager.ViewModel.CreateGroup(name.Trim(), group);
                ViewModelManager.ViewModel.RebuildFilteredTasks();
            };

            var miRename = new MenuItem { Header = "重命名" };
            miRename.Click += (_, _) =>
            {
                string? name = Microsoft.VisualBasic.Interaction.InputBox("请输入新名称：", "重命名分组", group.Name);
                if (string.IsNullOrWhiteSpace(name)) return;
                ViewModelManager.ViewModel.RenameGroup(group, name.Trim());
            };

            var miExport = new MenuItem { Header = "导出翻译结果…" };
            miExport.Click += (_, _) => ViewModelManager.ViewModel.ExportGroupResults(group);

            var miDelete = new MenuItem { Header = "删除分组（任务移至未分组）", Foreground = Brushes.IndianRed };
            miDelete.Click += (_, _) =>
            {
                int taskCount = group.CountAllTasks();
                string msg = taskCount > 0
                    ? $"确定删除分组「{group.Name}」？\n\n该分组下共有 {taskCount} 个任务，将被移至「未分组」。"
                    : $"确定删除分组「{group.Name}」？";

                if (!Window_Message.ShowDialog("确认删除", msg, isSingleBtn: false))
                    return;

                ViewModelManager.ViewModel.DeleteGroup(group);
                ViewModelManager.ViewModel.RebuildFilteredTasks();
            };

            var miDeleteAll = new MenuItem { Header = "删除分组与内容", Foreground = Brushes.IndianRed };
            miDeleteAll.Click += (_, _) =>
            {
                int taskCount = group.CountAllTasks();
                string msg = taskCount > 0
                    ? $"确定删除分组「{group.Name}」及其全部内容？\n\n该分组（含子分组）下共 {taskCount} 个任务将被彻底删除，磁盘上的翻译数据一并清除，此操作不可恢复！"
                    : $"确定删除分组「{group.Name}」？";

                if (!Window_Message.ShowDialog("确认删除分组与内容", msg, isSingleBtn: false))
                    return;

                _ = ViewModelManager.ViewModel.DeleteGroupWithContents(group);
            };

            menu.Items.Add(miNew);
            menu.Items.Add(miRename);
            menu.Items.Add(new Separator());
            menu.Items.Add(miExport);
            menu.Items.Add(new Separator());
            menu.Items.Add(miDelete);
            menu.Items.Add(miDeleteAll);

            return menu;
        }
    }
}
