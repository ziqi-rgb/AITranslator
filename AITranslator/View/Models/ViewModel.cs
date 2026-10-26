using AITranslator.Exceptions;
using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.Tools;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LLama.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using static LLama.Common.ChatHistory;
using Path = System.IO.Path;

namespace AITranslator.View.Models
{
    /// <summary>
    /// 通讯类型
    /// </summary>
    public enum CommunicatorType
    {
        LLama,
        OpenAI
    }
    /// <summary>
    /// 任务排序模式
    /// </summary>
    public enum TaskSortMode
    {
        Manual,
        Name,
        ImportTime
    }
    /// <summary>
    /// 用于界面绑定的ViewModel
    /// </summary>
    public partial class ViewModel : ObservableObject
    {
        /// <summary>
        /// 版本号
        /// </summary>
        [ObservableProperty]
        private string? version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath).FileVersion;
        /// <summary>
        /// 是否为测试版本
        /// </summary>
        [ObservableProperty]
        private bool isBeta = false;
        /// <summary>
        /// 已同意声明
        /// </summary>
        [ObservableProperty]
        private bool agreedStatement;
        /// <summary>
        /// 添加任务下拉框显示
        /// </summary>
        [ObservableProperty]
        private bool addTaskPopupShow;
        /// <summary>
        /// 控制台输出内容
        /// </summary>
        [ObservableProperty]
        private ObservableQueue<string> consoles = new ObservableQueue<string>(200);
        /// <summary>
        /// 是否手动翻译中
        /// </summary>
        [ObservableProperty]
        private bool manualTranslating;
        /// <summary>
        /// 当前任务（保留用于向后兼容，始终指向第一个活跃任务）
        /// </summary>
        [ObservableProperty]
        private TranslationTask? activeTask;
        /// <summary>
        /// 当前活跃任务数量（用于绑定：ActiveTaskCount > 0 替代 ActiveTask != null）
        /// </summary>
        [ObservableProperty]
        private int activeTaskCount;
        /// <summary>
        /// 并发控制锁
        /// </summary>
        private readonly object _concurrencyLock = new();
        /// <summary>
        /// 排序模式
        /// </summary>
        [ObservableProperty]
        private TaskSortMode taskSortMode = TaskSortMode.ImportTime;
        /// <summary>
        /// 排序方向（true=升序）
        /// </summary>
        [ObservableProperty]
        private bool sortAscending = true;
        /// <summary>
        /// 未完成任务
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TranslationTask> unfinishedTasks = new ObservableCollection<TranslationTask>();
        /// <summary>
        /// 已完成任务
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TranslationTask> completedTasks = new ObservableCollection<TranslationTask>();
        /// <summary>
        /// 对话格式模板
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Template> templateConfigs = new ObservableCollection<Template>();
        /// <summary>
        /// 对话格式模板
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Template> instructTemplate = new ObservableCollection<Template>();
        /// <summary>
        /// 自定义模板
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TemplateDic> templateDics = new ObservableCollection<TemplateDic>();
        /// <summary>
        /// 通讯器参数模板
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Template> communicatorParams = new ObservableCollection<Template>();
        /// <summary>
        /// 默认通讯器参数模板
        /// </summary>
        [ObservableProperty]
        private Template? defaultCommunicatorParam;
        /// <summary>
        /// 通讯器模板
        /// </summary>
        [ObservableProperty]
        private ViewModel_Communicator communicator = new ViewModel_Communicator();


        ///// <summary>
        ///// 是否使用OpenAI接口的第三方加载库
        ///// </summary>
        //[ObservableProperty]
        //private CommunicatorType communicatorType;
        ///// <summary>
        ///// LLama模型加载器ViewModel
        ///// </summary>
        //[ObservableProperty]
        //private ViewModel_CommunicatorLLama communicatorLLama_ViewModel = new ViewModel_CommunicatorLLama();
        ///// <summary>
        ///// OpenAI模型加载器ViewModel
        ///// </summary>
        //[ObservableProperty]
        //private ViewModel_CommunicatorOpenAI communicatorOpenAI_ViewModel = new ViewModel_CommunicatorOpenAI();
        /// <summary>
        /// 设置界面的ViewModel
        /// </summary>
        [ObservableProperty]
        private ViewModel_SetView setView_ViewModel = new ViewModel_SetView();

        //UI线程
        internal Dispatcher Dispatcher;

        public void AddTask(TranslationTask task)
        {
            if (task.State == TaskState.Completed)
                CompletedTasks.Add(task);
            else
                UnfinishedTasks.Add(task);

            // 新任务默认归入"未分组"（批量导入时会被移到具体分组）
            UngroupedGroup.AddTask(task);

            // 注意：这里不逐次调用 RebuildFilteredTasks()。
            // 启动/批量导入时会连续多次 AddTask，若在每次调用后都 Clear+重建
            // FilteredUnfinishedTasks，会导致正在枚举该集合的 ListBox 抛出
            // "Collection was modified" 异常。统一由 LoadGroups()/批量导入结束处刷新一次即可。
        }

        /// <summary>
        /// 对未完成任务列表按当前排序模式排序（O(N)：一次排序 + Move 归位）
        /// </summary>
        public void SortTasks()
        {
            if (TaskSortMode == TaskSortMode.Manual)
                return;

            List<TranslationTask> sorted = TaskSortMode switch
            {
                TaskSortMode.Name => SortAscending
                    ? UnfinishedTasks.OrderBy(t => t.FileName).ToList()
                    : UnfinishedTasks.OrderByDescending(t => t.FileName).ToList(),
                TaskSortMode.ImportTime => SortAscending
                    ? UnfinishedTasks.OrderBy(GetTaskImportTime).ToList()
                    : UnfinishedTasks.OrderByDescending(GetTaskImportTime).ToList(),
                _ => UnfinishedTasks.ToList(),
            };

            ApplySortedOrder(UnfinishedTasks, sorted);

            // UI 绑定的是过滤列表；若当前显示全部（SelectedGroup==null），同步应用新顺序
            if (SelectedGroup == null && FilteredUnfinishedTasks.Count == UnfinishedTasks.Count)
                ApplySortedOrder(FilteredUnfinishedTasks, sorted);
        }

        /// <summary>
        /// 将集合按 target 顺序重排（参考相等，O(N)）
        /// </summary>
        static void ApplySortedOrder(ObservableCollection<TranslationTask> collection, List<TranslationTask> target)
        {
            if (collection.Count != target.Count)
                return;

            var targetIndex = new Dictionary<TranslationTask, int>();
            for (int i = 0; i < target.Count; i++)
                targetIndex[target[i]] = i;

            for (int i = 0; i < collection.Count; i++)
            {
                if (targetIndex[collection[i]] == i)
                    continue;
                int currentIndex = collection.IndexOf(collection[i]);
                if (currentIndex != i)
                    collection.Move(currentIndex, i);
            }
        }

        /// <summary>
        /// 获取任务导入时间（文件夹创建时间，启动加载时已缓存到内存）
        /// </summary>
        static DateTime GetTaskImportTime(TranslationTask task) => task.ImportTime;

        /// <summary>
        /// 手动重排任务（供拖放使用）
        /// </summary>
        public void ReorderTask(int fromIndex, int toIndex)
        {
            if (fromIndex < 0 || fromIndex >= UnfinishedTasks.Count ||
                toIndex < 0 || toIndex >= UnfinishedTasks.Count ||
                fromIndex == toIndex)
                return;
            UnfinishedTasks.Move(fromIndex, toIndex);
        }

        /// <summary>
        /// 尝试获取并发槽位。成功返回 true 并递增 ActiveTaskCount；
        /// 失败（已达最大并发数）返回 false。
        /// </summary>
        public bool TryAcquireConcurrencySlot(TranslationTask task)
        {
            lock (_concurrencyLock)
            {
                int max = SetView_ViewModel.MaxConcurrency;
                if (ActiveTaskCount >= max)
                    return false;
                ActiveTaskCount++;
                ActiveTask ??= task;
                return true;
            }
        }

        /// <summary>
        /// 释放并发槽位，递减 ActiveTaskCount，并尝试调度等待中的任务。
        /// </summary>
        public void ReleaseConcurrencySlot(TranslationTask task)
        {
            lock (_concurrencyLock)
            {
                ActiveTaskCount--;
                if (ActiveTaskCount < 0)
                    ActiveTaskCount = 0;
                if (ActiveTask == task)
                    ActiveTask = null;
            }
            // 调度下一个等待任务
            DispatchWaitingTasks();
        }

        /// <summary>
        /// 从等待队列中启动尽可能多的任务（受 MaxConcurrency 限制）
        /// </summary>
        public void DispatchWaitingTasks()
        {
            // 获取等待中的任务列表快照
            List<TranslationTask> waitingTasks;
            lock (_concurrencyLock)
            {
                waitingTasks = UnfinishedTasks
                    .Where(s => s.State == TaskState.WaitTranslate)
                    .ToList();
            }
            foreach (var task in waitingTasks)
            {
                // 如果已达并发上限，Start() 内部会重新设为 WaitTranslate
                if (ActiveTaskCount >= SetView_ViewModel.MaxConcurrency)
                    break;
                task.Start(false);
            }
        }

        partial void OnTaskSortModeChanged(TaskSortMode value)
        {
            SortTasks();
        }

        partial void OnSortAscendingChanged(bool value)
        {
            SortTasks();
        }

        [RelayCommand]
        private void ChangeSortMode(TaskSortMode mode)
        {
            TaskSortMode = mode;
        }

        public async Task RemoveTask(TranslationTask task, bool showErrorMsg = true, bool askDeleteEmptyGroup = true, bool skipSaveGroups = false)
        {
            try
            {
                // 记录任务原属分组（用于删除后判断该分组是否已无任务）
                TaskGroup? originalGroup = askDeleteEmptyGroup ? FindGroupOfTask(task) : null;

                if (task.State == TaskState.Translating)
                    await task.Pause();
                while (task.State == TaskState.WaitPause)
                    await Task.Delay(50);

                string dicName = PublicParams.GetDicName(task.DicName);
                if (Directory.Exists(dicName))
                    FileSystem.DeleteDirectory(PublicParams.GetDicName(task.DicName), UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

                if (task.State == TaskState.Completed)
                    ViewModelManager.ViewModel.CompletedTasks.Remove(task);
                else
                    ViewModelManager.ViewModel.UnfinishedTasks.Remove(task);

                // 同步从分组中移除并保存（批量删除时跳过逐次写盘，由调用方收尾统一落盘）
                RemoveTaskFromAllGroups(task);
                if (!skipSaveGroups)
                    SaveGroups();

                // 增量刷新过滤列表（避免全列表重建）
                OnTaskRemoved(task);

                ViewModelManager.WriteLine($"[{DateTime.Now:G}]已删除任务[{task.FileName}]");

                // 自动创建的分组若已无任务，询问是否删除该分组
                if (originalGroup != null &&
                    originalGroup.IsAutoCreated &&
                    !originalGroup.IsUngrouped &&
                    originalGroup.Tasks.Count == 0)
                {
                    PromptDeleteEmptyGroup(originalGroup);
                }
            }
            catch (Exception err)
            {
                string error = $"清除任务[{task.FileName}]失败:{err}";
                ViewModelManager.WriteLine($"[{DateTime.Now:G}]{error}");
                if (showErrorMsg)
                    Window_Message.ShowDialog("错误", "清除任务失败，请尝试手动删除");
            }
        }

        /// <summary>
        /// 询问是否删除已无任务的自动创建分组（连同子分组与内容一并删除）
        /// </summary>
        void PromptDeleteEmptyGroup(TaskGroup group)
        {
            int subTaskCount = group.CountAllTasks();
            string msg = subTaskCount > 0
                ? $"分组「{group.Name}」已无任务，但其子分组下还有 {subTaskCount} 个任务。\n\n是否删除该分组及其全部内容？"
                : $"分组「{group.Name}」已无任务。\n\n是否删除该分组？";

            if (Window_Message.ShowDialog("删除分组", msg, isSingleBtn: false))
                _ = DeleteGroupWithContents(group);
        }

        /// <summary>
        /// 在控制台中打印日志
        /// </summary>
        /// <param name="Msg">要被打印的日志</param>
        internal void ConsoleWriteLine(string Msg)
        {
            if (Dispatcher.HasShutdownStarted)
                return;
            try
            {
                Dispatcher.Invoke(() => Consoles.Enqueue(Msg));
            }
            catch (TaskCanceledException)//如果此时UI线程已经停止，则略过
            {
                return;
            }
        }

        [RelayCommand]
        private void CloseAddTaskPopup() => AddTaskPopupShow = false;

        [RelayCommand]
        private void StartAll()
        {
            if (UnfinishedTasks.Count == 0)
                return;

            if (Communicator.CommunicatorType == CommunicatorType.LLama && !Communicator.ModelLoaded)
            {
                Window_Message.ShowDialog("提示", "请先加载模型！");
                return;
            }

            // 将所有未完成任务标记为等待翻译，然后由 DispatchWaitingTasks 按并发数启动
            foreach (var _task in UnfinishedTasks)
            {
                if (_task.State == TaskState.Initialized || _task.State == TaskState.Pause)
                    _task.State = TaskState.WaitTranslate;
            }
            DispatchWaitingTasks();
        }

        [RelayCommand]
        private void PauseAll()
        {
            // 收集所有正在翻译/清理中的任务
            List<TranslationTask> activeTasks;
            lock (_concurrencyLock)
            {
                activeTasks = UnfinishedTasks
                    .Where(s => s.State == TaskState.Translating || s.State == TaskState.Cleaning)
                    .ToList();
            }

            // 先暂停非活跃任务（WaitTranslate 状态的）
            foreach (var _task in UnfinishedTasks.Where(s => s.State == TaskState.WaitTranslate))
                _task.State = TaskState.Pause;

            // 暂停所有活跃任务
            foreach (var task in activeTasks)
                task.Pause();
        }

        [RelayCommand]
        private void AddTaskFromFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog()
            {
                Title = "请选择待翻译的文本文件",
                Multiselect = true,
                FileName = "Select a file",
                Filter = "待翻译文件(*.json;*.txt;*.srt)|*.json;*.txt;*.srt",
            };

            if (!openFileDialog.ShowDialog()!.Value)
                return;

            // 后台执行文件 I/O，避免 UI 卡死
            string[] files = openFileDialog.FileNames.ToArray();
            Task.Run(() =>
            {
                foreach (var fileName in files)
                    BackgroundImportFile(fileName);
            });
        }

        /// <summary>
        /// 检查指定路径是否已被导入过（跨 Unfinished + Completed）
        /// </summary>
        bool IsDuplicatePath(string fullPath)
        {
            return UnfinishedTasks.Concat<TranslationTask>(CompletedTasks)
                .Any(t => t.SourcePath != null &&
                          string.Equals(t.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 后台导入单个文件（I/O 在后台线程，AddTask 回 UI 线程）
        /// </summary>
        public void BackgroundImportFile(string fileName)
        {
            try
            {
                string fullPath = Path.GetFullPath(fileName);

                // 去重检查（需在 UI 线程访问集合）
                bool isDuplicate = Dispatcher.Invoke(() => IsDuplicatePath(fullPath));
                if (isDuplicate)
                    return;

                FileInfo info = new FileInfo(fullPath);
                TranslateDataType type = info.Extension.ToLower() switch
                {
                    ".json" => TranslateDataType.KV,
                    ".srt" => TranslateDataType.Srt,
                    ".txt" => TranslateDataType.Txt,
                    _ => TranslateDataType.Unknow
                };
                TranslationTask task = new TranslationTask(type, info.FullName, info.Name);
                Dispatcher.Invoke(() => AddTask(task));
            }
            catch (KnownException err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"错误:{err.Message}"));
            }
            catch (Exception err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"未知错误:{err}"));
            }
        }

        [RelayCommand]
        private void AddTaskFromFolder()
        {
            OpenFolderDialog openFolderDialog = new OpenFolderDialog()
            {
                Title = "请选择包含翻译源文件的文件夹（将递归扫描子文件夹）",
                Multiselect = false
            };

            if (!openFolderDialog.ShowDialog()!.Value)
                return;

            string folder = openFolderDialog.FolderName;
            Task.Run(() => BackgroundImportFolder(folder));
        }

        /// <summary>
        /// 后台导入文件夹：自动检测格式（CSV/JSON/SRT/TXT），纯 CSV 文件夹按 T++ 任务导入，其余按所选格式递归导入
        /// </summary>
        public void BackgroundImportFolder(string folderName)
        {
            try
            {
                string fullPath = Path.GetFullPath(folderName);

                // 去重检查（需在 UI 线程访问集合）
                bool isDuplicate = Dispatcher.Invoke(() => IsDuplicatePath(fullPath));
                if (isDuplicate)
                    return;

                // 扫描格式并让用户选择（UI 线程弹窗；仅一种格式时直接选定）
                string? selectedExt = Dispatcher.Invoke(() => SelectFolderFormat(fullPath));
                if (selectedExt is null)
                    return;

                if (selectedExt == ".csv")
                    ImportTppFolder(fullPath);
                else
                    ImportFolderByExtension(fullPath, selectedExt);
            }
            catch (KnownException err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"错误:{err.Message}"));
            }
            catch (Exception err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"未知错误:{err}"));
            }
        }

        /// <summary>
        /// 将整个文件夹作为一个 T++ CSV 翻译任务导入
        /// </summary>
        void ImportTppFolder(string fullPath)
        {
            TranslationTask task = new TranslationTask(TranslateDataType.Tpp, fullPath, Path.GetFileName(fullPath.TrimEnd('\\', '/')));
            Dispatcher.Invoke(() => AddTask(task));
        }

        /// <summary>
        /// 扫描目录下各格式文件数量：仅一种格式时直接返回；多种格式时弹出选择窗口
        /// </summary>
        /// <param name="folderPath"></param>
        /// <returns>选定的扩展名（含点号）；用户取消或无受支持文件时返回 null</returns>
        string? SelectFolderFormat(string folderPath)
        {
            var fileCounts = new Dictionary<string, int> { [".csv"] = 0, [".json"] = 0, [".srt"] = 0, [".txt"] = 0 };
            try
            {
                foreach (var f in Directory.EnumerateFiles(folderPath, "*", System.IO.SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (fileCounts.ContainsKey(ext))
                        fileCounts[ext]++;
                }
            }
            catch { }

            var available = fileCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            if (available.Count == 0)
            {
                Window_Message.ShowDialog("提示", "未找到可导入的文件（csv / json / srt / txt）");
                return null;
            }

            // 仅一种格式，直接选定，不弹窗
            if (available.Count == 1)
                return available[0];

            // 多种格式，由用户选择（默认选中数量最多的）
            string defaultExt = fileCounts.OrderByDescending(kv => kv.Value).First().Key;
            var selectWin = new Window_SelectFormat(fileCounts, defaultExt) { Owner = System.Windows.Application.Current.MainWindow };
            if (selectWin.ShowDialog() != true)
                return null;

            return selectWin.SelectedExtension;
        }

        #region 批量文件夹导入

        /// <summary>
        /// 批量导入文件夹：递归扫描指定扩展名的文件，保留目录结构为分组树
        /// </summary>
        [RelayCommand]
        private void AddTaskFromBatchFolder()
        {
            OpenFolderDialog openFolderDialog = new OpenFolderDialog()
            {
                Title = "请选择包含翻译源文件的文件夹（将递归扫描子文件夹）",
                Multiselect = false
            };

            if (!openFolderDialog.ShowDialog()!.Value)
                return;

            string folder = openFolderDialog.FolderName;
            Task.Run(() => BackgroundImportBatchFolder(folder));
        }

        /// <summary>
        /// 后台执行批量文件夹导入：选择格式后递归导入（csv 按 T++ 任务，其余按单文件格式）
        /// </summary>
        public void BackgroundImportBatchFolder(string folderPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(folderPath);

                bool isDuplicate = Dispatcher.Invoke(() => IsDuplicatePath(fullPath));
                if (isDuplicate)
                    return;

                // 扫描格式并让用户选择（UI 线程弹窗）
                string? selectedExt = Dispatcher.Invoke(() => SelectFolderFormat(fullPath));
                if (selectedExt is null)
                    return;

                if (selectedExt == ".csv")
                    ImportTppFolder(fullPath);
                else
                    ImportFolderByExtension(fullPath, selectedExt);
            }
            catch (KnownException err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"错误:{err.Message}"));
            }
            catch (Exception err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"未知错误:{err}"));
            }
        }

        /// <summary>
        /// 按指定扩展名递归扫描文件夹导入，保留目录结构为分组树
        /// </summary>
        void ImportFolderByExtension(string fullPath, string extension)
        {
            try
            {
                var files = Directory.EnumerateFiles(fullPath, $"*{extension}", System.IO.SearchOption.AllDirectories)
                    .OrderBy(f => f).ToList();

                if (files.Count == 0)
                {
                    Dispatcher.Invoke(() => Window_Message.ShowDialog("提示", $"未找到 {extension} 文件"));
                    return;
                }

                // 按相对目录构建分组缓存（包含所选文件夹自身名称作为顶级分组）
                var groupCache = new Dictionary<string, TaskGroup>();
                int importedCount = 0;
                string baseName = Path.GetFileName(fullPath.TrimEnd('\\', '/'));

                foreach (var file in files)
                {
                    bool isDuplicate = Dispatcher.Invoke(() => IsDuplicatePath(file));
                    if (isDuplicate) continue;

                    // 计算相对目录路径（含所选文件夹自身名称，如 "2026-10-11/小说名称"）
                    string relativeDir = "";
                    try
                    {
                        string baseDir = fullPath.TrimEnd('\\', '/');
                        string fileDir = Path.GetDirectoryName(file)?.TrimEnd('\\', '/') ?? "";
                        if (fileDir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                            relativeDir = fileDir.Substring(baseDir.Length).TrimStart('\\', '/');
                        if (!string.IsNullOrEmpty(baseName))
                            relativeDir = string.IsNullOrEmpty(relativeDir) ? baseName : $"{baseName}/{relativeDir}";
                    }
                    catch { }

                    // 获取或创建目标分组（涉及 UI 绑定集合，必须在 UI 线程执行）
                    TaskGroup targetGroup = null!;

                    // 创建任务（根据扩展名映射正确的数据类型）
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    TranslateDataType dataType = extension.ToLowerInvariant() switch
                    {
                        ".json" => TranslateDataType.KV,
                        ".srt" => TranslateDataType.Srt,
                        _ => TranslateDataType.Txt
                    };
                    TranslationTask task = new TranslationTask(dataType, file, fileName);

                    Dispatcher.Invoke(() =>
                    {
                        AddTask(task);
                        if (targetGroup == null)
                        {
                            targetGroup = GetOrCreateGroupPath(groupCache, relativeDir);
                            RemoveTaskFromAllGroups(task);
                            targetGroup.AddTask(task);
                        }
                    });

                    importedCount++;
                }

                Dispatcher.Invoke(() =>
                {
                    SaveGroups();
                    RebuildFilteredTasks();
                    Window_Message.ShowDialog("完成", $"成功导入 {importedCount} 个文件（共扫描 {files.Count} 个）");
                });
            }
            catch (Exception err)
            {
                Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"批量导入失败: {err.Message}"));
            }
        }

        /// <summary>
        /// 根据相对路径获取或创建分组链（如 "A/B/C" → A > B > C）
        /// </summary>
        TaskGroup GetOrCreateGroupPath(Dictionary<string, TaskGroup> groupCache, string relativeDir)
        {
            if (groupCache.TryGetValue(relativeDir, out var cached))
                return cached;

            var parts = relativeDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            TaskGroup? current = null;
            string pathSoFar = "";

            foreach (var part in parts)
            {
                pathSoFar = pathSoFar.Length == 0 ? part : pathSoFar + "/" + part;

                if (groupCache.TryGetValue(pathSoFar, out var existing))
                {
                    current = existing;
                    continue;
                }

                // 查找当前父级下是否已有同名子分组
                TaskGroup? found = null;
                if (current != null)
                    found = current.Children.FirstOrDefault(c => c.Name == part);
                else
                    found = RootGroups.FirstOrDefault(c => c.Name == part);

                if (found == null)
                {
                    var newGroup = new TaskGroup { Name = part, IsAutoCreated = true };
                    if (current != null)
                        current.AddChild(newGroup);
                    else
                    {
                        RootGroups.Add(newGroup);
                        TreeRootNodes.Add(newGroup);
                    }
                    found = newGroup;
                }

                groupCache[pathSoFar] = found;
                current = found;
            }

            return current!;
        }

        #endregion

        #region 分组导出

        /// <summary>
        /// 按分组结构导出该分组及所有子分组的翻译结果到指定目录。
        /// 目录结构与分组树一致（被点击分组自身名称作为顶层文件夹），仅导出翻译结果，不含其它文件。
        /// </summary>
        public void ExportGroupResults(TaskGroup group)
        {
            int taskCount = group.CountAllTasks();
            if (taskCount == 0)
            {
                Window_Message.ShowDialog("提示", "该分组下没有任务");
                return;
            }

            OpenFolderDialog dlg = new OpenFolderDialog
            {
                Title = $"选择导出目录（将按分组结构导出 {taskCount} 个任务的翻译结果）",
                Multiselect = false
            };
            if (dlg.ShowDialog() != true)
                return;
            string destDir = Path.GetFullPath(dlg.FolderName);

            // UI 线程收集 (任务, 相对目录) 列表，避免后台遍历绑定集合
            var items = new List<(TranslationTask task, string relDir)>();
            CollectExportItems(group, group.Name, items);

            Task.Run(() =>
            {
                try
                {
                    int exported = 0, skipped = 0;
                    foreach (var (task, relDir) in items)
                    {
                        try
                        {
                            string targetDir = Path.Combine(destDir, relDir.Replace('/', Path.DirectorySeparatorChar));
                            if (task.ExportResult(targetDir)) exported++;
                            else skipped++;
                        }
                        catch { skipped++; }
                    }

                    Dispatcher.Invoke(() =>
                    {
                        string msg = skipped > 0
                            ? $"已导出 {exported} 个任务，{skipped} 个无翻译结果（尚未合并或已完成）\n\n目录：{destDir}"
                            : $"成功导出 {exported} 个任务的翻译结果\n\n目录：{destDir}";
                        Window_Message.ShowDialog("完成", msg);
                    });
                }
                catch (Exception err)
                {
                    Dispatcher.Invoke(() => Window_Message.ShowDialog("错误", $"导出失败: {err.Message}"));
                }
            });
        }

        /// <summary>
        /// 递归收集分组树中的任务及其相对目录路径（含被点击分组自身名称）
        /// </summary>
        void CollectExportItems(TaskGroup group, string relPath, List<(TranslationTask task, string relDir)> items)
        {
            foreach (var task in group.Tasks)
                items.Add((task, relPath));
            foreach (var child in group.Children)
                CollectExportItems(child, $"{relPath}/{child.Name}", items);
        }

        #endregion

        public void CreatAddTaskFromFile(string fileName)
        {
            ExpandedFuncs.TryExceptions(() =>
            {
                FileInfo info = new FileInfo(fileName);
                TranslateDataType type = info.Extension.ToLower() switch
                {
                    ".json" => TranslateDataType.KV,
                    ".srt" => TranslateDataType.Srt,
                    ".txt" => TranslateDataType.Txt,
                    _ => TranslateDataType.Unknow
                };
                TranslationTask task = new TranslationTask(type, info.FullName, info.Name);

                AddTask(task);
            });
        }

        public void CreatAddTaskFromFolder(string folderName)
        {
            ExpandedFuncs.TryExceptions(() =>
            {
                DirectoryInfo info = new DirectoryInfo(folderName);
                TranslationTask task = new TranslationTask(TranslateDataType.Tpp, info.FullName, info.Name);
                AddTask(task);
            });
        }

        [RelayCommand]
        private void RemoveAll()
        {
            if (CompletedTasks.Count == 0)
                return;

            Window_ConfirmClear window_ConfirmClear = new Window_ConfirmClear();
            window_ConfirmClear.Owner = Window_Message.DefaultOwner;
            if (!window_ConfirmClear.ShowDialog()!.Value)
                return;

            // 批量删除：跳过逐次分组写盘，收尾统一落盘一次（避免 N × 整树序列化）
            var toRemove = new List<TranslationTask>(CompletedTasks);
            foreach (var task in toRemove)
                RemoveTask(task, false, askDeleteEmptyGroup: false, skipSaveGroups: true).Wait();

            FlushGroups();
        }

        [RelayCommand]
        private void ShowDeclare()
        {
            Window_Message.ShowDialog("软件声明", "\r\nAITranslator皆仅供学习交流使用，开发者对使用本软件造成的问题不负任何责任。\r\n\r\n使用此软件翻译时，请遵守所使用模型或平台的相关规定\r\n\r\n所有使用本软件翻译的文件与其衍生成果均禁止任何形式的商用！\r\n\r\n所有使用本软件翻译的文件与其衍生成果均与软件制作者无关，请各位遵守法律，合法翻译。\r\n\r\n本软件为免费使用，如果您是付费购买的，请立刻举报您购买的平台");
        }


        [RelayCommand]
        private void CheckUpdate()
        {
            Window_CheckUpdate window_CheckUpdate = new Window_CheckUpdate();
            window_CheckUpdate.Owner = Window_Message.DefaultOwner;
            window_CheckUpdate.ShowDialog();
        }

        [RelayCommand]
        private void ClearLogs()
        {
            Consoles.Clear();
        }

        [RelayCommand]
        private void OpenInstructTemplateFolder()
        {
            string path = Path.GetFullPath(PublicParams.InstructTemplateDic);
            Process.Start("explorer.exe", path);
        }

        [RelayCommand]
        private void OpenInstructTemplateFile()
        {
            if (ViewModelManager.ViewModel.Communicator.CurrentInstructTemplate is null)
            {
                Window_Message.ShowDialog("提示", "请先选择对话格式");
                return;
            }
            string path = Path.GetFullPath($"{PublicParams.InstructTemplateDic}\\{ViewModelManager.ViewModel.Communicator.CurrentInstructTemplate.Name}.csx");
            Process.Start("explorer.exe", path);
        }

        [RelayCommand]
        private void TestInstructTemplate()
        {
            try
            {
                if (ViewModelManager.ViewModel.Communicator.CurrentInstructTemplate is null)
                {
                    Window_Message.ShowDialog("提示", "请先选择对话格式");
                    return;
                }
                Script<string> script = CSharpScript.Create<string>(File.ReadAllText($"{PublicParams.InstructTemplateDic}\\{ViewModelManager.ViewModel.Communicator.CurrentInstructTemplate.Name}.csx"), ScriptOptions.Default.WithReferences(typeof(Message).Assembly, typeof(StringBuilder).Assembly), globalsType: typeof(InstructScriptInput));
                List<Message> restmessages = [new(AuthorRole.System, "这里是System语句"), new(AuthorRole.User, "这里是User语句1"), new(AuthorRole.Assistant, "这里是Assistant语句1"), new(AuthorRole.User, "这里是User语句2")];
                InstructScriptInput testGloableClass = new InstructScriptInput() { Messages = restmessages };
                string result = script.RunAsync(testGloableClass).Result.ReturnValue;
                Window_Message.ShowDialog("查看对话格式", result);
            }
            catch (CompilationErrorException error)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("对话格式脚本错误:");
                for (int i = 0; i < error.Diagnostics.Length; i++)
                {
                    Diagnostic diagnostic = error.Diagnostics[i];
                    if (i != error.Diagnostics.Length - 1)
                        sb.AppendLine(diagnostic.ToString());
                    else
                        sb.Append(diagnostic.ToString());
                }
                Window_Message.ShowDialog("错误", sb.Remove(sb.Length - 2, 2).ToString());
            }
            catch (Exception error)
            {
                Window_Message.ShowDialog("未知错误", error.ToString());
            }
        }



    }
}
