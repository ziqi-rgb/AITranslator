using AITranslator.Exceptions;
using AITranslator.Translator.Tools;
using AITranslator.View.Models;
using AITranslator.View.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace AITranslator.View.UserControls
{
    /// <summary>
    /// UserControl_TranlatingView.xaml 的交互逻辑
    /// </summary>
    public partial class UserControl_TranlatingView : UserControl
    {
        Point _dragStartPoint;

        public UserControl_TranlatingView()
        {
            InitializeComponent();
            Loaded += (_, _) => UpdateSortDirectionButton();
        }

        private void Button_StartOrPause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TranslationTask task)
            {
                if (btn.ToolTip.ToString() == "暂停")
                    task.Pause();
                else
                {
                    ViewModel vm = ViewModelManager.ViewModel;
                    if (vm.Communicator.CommunicatorType == CommunicatorType.LLama && !vm.Communicator.ModelLoaded)
                    {
                        Window_Message.ShowDialog("提示", "请先加载模型！");
                        return;
                    }
                    task.Start();
                }
            }
        }

        /// <summary>
        /// 切换升降序
        /// </summary>
        private void Button_ToggleSort_Click(object sender, RoutedEventArgs e)
        {
            var vm = ViewModelManager.ViewModel;
            vm.SortAscending = !vm.SortAscending;
            UpdateSortDirectionButton();
        }

        void UpdateSortDirectionButton()
        {
            if (btn_SortDir is null) return;
            var vm = ViewModelManager.ViewModel;
            btn_SortDir.Content = vm.SortAscending ? "↑ 升序" : "↓ 降序";
        }

        // ── 拖放重排 ──

        private void ListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void ListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            ListBox listBox = sender as ListBox;
            if (listBox is null) return;

            ListBoxItem? item = FindListBoxItem(listBox, e.GetPosition(listBox));
            if (item is null) return;

            TranslationTask? task = item.DataContext as TranslationTask;
            if (task is null) return;

            DragDrop.DoDragDrop(item, task, DragDropEffects.Move);
        }

        private void ListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TranslationTask)))
                e.Effects = DragDropEffects.None;
        }

        private void ListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TranslationTask)))
                return;

            TranslationTask? draggedTask = e.Data.GetData(typeof(TranslationTask)) as TranslationTask;
            if (draggedTask is null) return;

            ListBox listBox = sender as ListBox;
            if (listBox is null) return;

            // 找到拖放目标位置
            ListBoxItem? targetItem = FindListBoxItem(listBox, e.GetPosition(listBox));
            if (targetItem is null) return;

            TranslationTask? targetTask = targetItem.DataContext as TranslationTask;
            if (targetTask is null || targetTask == draggedTask) return;

            var vm = ViewModelManager.ViewModel;
            int fromIndex = vm.UnfinishedTasks.IndexOf(draggedTask);
            int toIndex = vm.UnfinishedTasks.IndexOf(targetTask);

            if (fromIndex < 0 || toIndex < 0) return;

            // 拖放即切换到手动模式
            vm.TaskSortMode = TaskSortMode.Manual;
            vm.ReorderTask(fromIndex, toIndex);
        }

        static ListBoxItem? FindListBoxItem(ListBox listBox, Point point)
        {
            var element = listBox.InputHitTest(point) as DependencyObject;
            while (element != null && !(element is ListBoxItem))
                element = VisualTreeHelper.GetParent(element);
            return element as ListBoxItem;
        }

        // ── 分组导航 ──

        bool _isCollapsed = false;

        /// <summary>
        /// TreeView 选中分组变化 → 过滤任务列表
        /// </summary>
        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var vm = ViewModelManager.ViewModel;
            vm.SelectedGroup = e.NewValue as TaskGroup;
        }

        /// <summary>
        /// 新建分组按钮（底部）
        /// </summary>
        private void Btn_CreateGroup_Click(object sender, RoutedEventArgs e)
        {
            var vm = ViewModelManager.ViewModel;
            string? name = Microsoft.VisualBasic.Interaction.InputBox("请输入分组名称：", "新建分组");
            if (string.IsNullOrWhiteSpace(name))
                return;

            TaskGroup? parent = vm.SelectedGroup;
            vm.CreateGroup(name.Trim(), parent);
            vm.RebuildFilteredTasks();
        }

        /// <summary>
        /// 折叠/展开分组栏（节省右侧空间）
        /// </summary>
        private void Btn_Collapse_Click(object sender, RoutedEventArgs e)
        {
            _isCollapsed = !_isCollapsed;
            if (_isCollapsed)
            {
                border_Groups.Visibility = Visibility.Collapsed;
                btn_Expand.Visibility = Visibility.Visible;
                col_Groups.Width = new GridLength(28);
            }
            else
            {
                border_Groups.Visibility = Visibility.Visible;
                btn_Expand.Visibility = Visibility.Collapsed;
                col_Groups.Width = new GridLength(150);
            }
        }

        // ── 分组右键菜单（在 code-behind 动态构建，避免 XAML 内联事件连接问题） ──

        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null) return;

            // 找到右键点击位置下的 TreeViewItem
            Point pos = Mouse.GetPosition(treeView);
            var tvi = GroupPanelHelper.FindItemAt(treeView, pos);

            if (tvi is null || tvi.DataContext is not TaskGroup group)
                return;

            tvi.ContextMenu = GroupPanelHelper.BuildContextMenu(group);
        }

        // ── 拖拽任务到分组（移动） ──

        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(TranslationTask)) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private void Tree_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(TranslationTask))) return;
            var task = e.Data.GetData(typeof(TranslationTask)) as TranslationTask;
            if (task is null) return;

            var treeView = sender as TreeView;
            if (treeView is null) return;

            Point pos = e.GetPosition(treeView);
            var element = treeView.InputHitTest(pos) as DependencyObject;
            while (element != null && !(element is TreeViewItem))
                element = VisualTreeHelper.GetParent(element);

            if (element is not TreeViewItem tvi || tvi.DataContext is not TaskGroup targetGroup)
                return;

            var vm = ViewModelManager.ViewModel;
            // MoveTaskToGroup 内部已做增量刷新，无需再全量重建
            vm.MoveTaskToGroup(task, targetGroup);
        }
    }
}
