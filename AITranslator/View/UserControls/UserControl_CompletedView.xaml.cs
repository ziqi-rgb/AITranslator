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
    /// UserControl_CompletedView.xaml 的交互逻辑
    /// </summary>
    public partial class UserControl_CompletedView : UserControl
    {
        public UserControl_CompletedView()
        {
            InitializeComponent();
        }

        // ── 分组导航 ──

        bool _isCollapsed = false;

        /// <summary>
        /// TreeView 选中分组变化 → 过滤已完成任务列表
        /// </summary>
        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var vm = ViewModelManager.ViewModel;
            vm.SelectedCompletedGroup = e.NewValue as TaskGroup;
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

            TaskGroup? parent = vm.SelectedCompletedGroup;
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

        /// <summary>
        /// 分组右键菜单（与未完成视图共用构建逻辑）
        /// </summary>
        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var treeView = sender as TreeView;
            if (treeView is null) return;

            Point pos = Mouse.GetPosition(treeView);
            var tvi = GroupPanelHelper.FindItemAt(treeView, pos);

            if (tvi is null || tvi.DataContext is not TaskGroup group)
                return;

            tvi.ContextMenu = GroupPanelHelper.BuildContextMenu(group);
        }
    }
}
