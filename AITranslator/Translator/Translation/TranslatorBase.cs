using AITranslator.EventArg;
using AITranslator.Exceptions;
using AITranslator.Translator.Models;
using AITranslator.Translator.Tools;
using AITranslator.View.Models;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AITranslator.Translator.TranslateData;
using AITranslator.Translator.Communicator;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Diagnostics;
using System.Windows.Markup;
using AITranslator.Mail;
using AITranslator.Translator.PostData;
using Microsoft.CodeAnalysis.Scripting;
using AITranslator.Translator.Pretreatment;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using System.IO;
using AITranslator.Translator.Persistent;
using FileNotFoundException = AITranslator.Exceptions.FileNotFoundException;

namespace AITranslator.Translator.Translation
{
    public sealed class VerificationScriptInput
    {
        public string Translated;
        public string Untranslated;
    }
    internal class TranslateResult
    {
        public bool IsPause { get; private set; }
        public bool IsKnownError { get; private set; }
        public string PauseMsg { get; private set; }
        public bool Save { get; private set; }

        public static TranslateResult Successful()
        {
            return new TranslateResult { IsPause = false, Save = true };
        }

        public static TranslateResult Pause(string Msg, bool isKnown, bool save)
        {
            return new TranslateResult { IsPause = true, PauseMsg = Msg, IsKnownError = isKnown, Save = save };
        }
    }

    public enum TryTranslateType
    {
        Mult,
        Single,
        Retry
    }
    public abstract class TranslatorBase
    {
        /// <summary>
        /// 翻译失败的数据数量
        /// </summary>
        internal abstract int FailedDataCount { get; }
        /// <summary>
        /// 翻译类型
        /// </summary>
        public abstract TranslateDataType Type { get; }
        /// <summary>
        /// 翻译数据
        /// </summary>
        internal ITranslateData TranslateData { get; }
        /// <summary>
        /// 翻译停止事件，用于通讯UI
        /// </summary>
        internal event EventHandler<TranslateStopEventArgs> Stoped;

        /// <summary>
        /// 翻译线程
        /// </summary>
        Task _task;

        CancellationTokenSource _cts;

        internal TranslationTask _translationTask;
        readonly Script<(bool, string)> _verification;
        readonly VerificationScriptInput _verificationScriptInput = new VerificationScriptInput();

        /// <summary>
        /// 历史上下文
        /// </summary>
        internal Queue<ExampleDialogue> _history = new Queue<ExampleDialogue>();

        /// <summary>
        /// 替换词字典
        /// </summary>
        internal Dictionary<string, string> _replaces = new Dictionary<string, string>();

        /// <summary>
        /// 示例对话
        /// </summary>
        internal ExampleDialogue[] _example;

        internal ICommunicator? _communicator;

        internal void TrigerStopedEvent(TranslateStopEventArgs args)
        {
            Stoped?.Invoke(this, args);
        }
        public TranslatorBase(TranslationTask task)
        {
            _translationTask = task;

            //创建提示词模板
            if (task.TemplateConfigParams.PromptTemplate is null)
                throw new KnownException($"请先配置提示词模板");
            string promptTemplatePath = PublicParams.GetTemplateFilePath(task.TemplateConfigParams.TemplateDic!, TemplateType.Prompt, task.TemplateConfigParams.PromptTemplate);
            if (!File.Exists(promptTemplatePath))
                throw new FileNotFoundException($"提示词模板[{task.TemplateConfigParams.PromptTemplate}]不存在，请确认文件是否已被删除");
            _example = JsonPersister.Load<ExampleDialogue[]>(promptTemplatePath);

            //创建校验规则模板
            if (task.TemplateConfigParams.VerificationTemplate is null)
                throw new KnownException($"请先配置校验规则模板");
            string verificationTemplatePath = PublicParams.GetTemplateFilePath(task.TemplateConfigParams.TemplateDic!, TemplateType.Verification, task.TemplateConfigParams.VerificationTemplate);
            if (!File.Exists(verificationTemplatePath))
                throw new FileNotFoundException($"校验规则模板[{task.TemplateConfigParams.VerificationTemplate}]不存在，请确认文件是否已被删除");
            _verification = CSharpScript.Create<(bool, string)>(File.ReadAllText(verificationTemplatePath), ScriptOptions.Default, globalsType: typeof(VerificationScriptInput));
            //创建Data
            TranslateData = task.TranslateType switch
            {
                TranslateDataType.KV => new KVTranslateData(task.DicName, task.FileName),
                TranslateDataType.Tpp => new TppTranslateData(task.DicName, task.FileName),
                TranslateDataType.Srt => new SrtTranslateData(task.DicName, task.FileName),
                TranslateDataType.Txt => new TxtTranslateData(task.DicName, task.FileName),
                _ => throw new KnownException("不支持的翻译文件类型"),
            };

            //填充替换词
            if (task.TemplateConfigParams.ReplaceTemplate is null)
                throw new KnownException($"请先配置替换词模板");
            string replaceTemplatePath = PublicParams.GetTemplateFilePath(task.TemplateConfigParams.TemplateDic!, TemplateType.Replace, task.TemplateConfigParams.ReplaceTemplate);
            if (!File.Exists(replaceTemplatePath))
                throw new FileNotFoundException($"替换词模板[{task.TemplateConfigParams.ReplaceTemplate}]不存在，请确认文件是否已被删除");
            _replaces = JsonPersister.Load<Dictionary<string, string>>(replaceTemplatePath);
            foreach (var replace in task.Replaces)
                _replaces[replace.Key] = replace.Value ?? string.Empty;

            //计算当前进度
            CalculateProgress();

        }


        /// <summary>
        /// 计算当前翻译进度
        /// </summary>
        internal void CalculateProgress()
        {
            _translationTask.Progress = TranslateData.GetProgress();
            _translationTask.SaveConfig(TaskState.Pause);
        }

        /// <summary>
        /// 启动翻译
        /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _task = Task.Factory.StartNew(() =>
            {
                try
                {
                    //如果不存在清理后文件，执行清理流程
                    if (!TranslateData.IsCleaned)
                    {
                        try
                        {
                            ViewModelManager.WriteLine($"[{DateTime.Now:G}]开始清理");

                            _translationTask.State = TaskState.Cleaning;

                            //检测配置模板里的规则模板文件夹是否配置
                            if (_translationTask.TemplateConfigParams.TemplateDic is null)
                                throw new KnownException($"请先配置模板文件夹");

                            string? cleanTemplateName = _translationTask.TemplateConfigParams.CleanTemplate;
                            //检测清理规则模板是否配置
                            if (string.IsNullOrWhiteSpace(cleanTemplateName))
                                throw new KnownException("请先配置好清理规则模板");

                            //检测清理规则模板文件是否存在
                            string cleanTemplatePath = PublicParams.GetTemplateFilePath(_translationTask.TemplateConfigParams.TemplateDic, TemplateType.Clean, cleanTemplateName);
                            if (!File.Exists(cleanTemplatePath))
                                throw new FileNotFoundException($"清理模板文件[{cleanTemplateName}]不存在，请确认配置文件是否已被删除");

                            switch (Type)
                            {
                                case TranslateDataType.KV:
                                    KVTranslateData.Clear(_translationTask.DicName, cleanTemplatePath, token);
                                    break;
                                case TranslateDataType.Tpp:
                                    TppTranslateData.Clear(_translationTask.DicName, cleanTemplatePath, token);
                                    break;
                                case TranslateDataType.Srt:
                                    SrtTranslateData.Clear(_translationTask.DicName, cleanTemplatePath, token);
                                    break;
                                case TranslateDataType.Txt:
                                    TxtTranslateData.Clear(_translationTask.DicName, cleanTemplatePath, token);
                                    break;
                                default:
                                    throw new KnownException("不支持的翻译文件类型");
                            }

                            TranslateData.ReloadData();
                        }
                        catch (OperationCanceledException)
                        {
                            if (_cts.Token.IsCancellationRequested)
                                throw new KnownException("按下暂停按钮");
                        }
                    }

                    _translationTask.State = TaskState.Translating;
                    ViewModelManager.WriteLine($"[{DateTime.Now:G}]开始翻译");
                    //创建连接客户端，设置超时时间10分钟
                    _communicator = ViewModelManager.ViewModel.Communicator.CommunicatorType switch
                    {
                        CommunicatorType.LLama => new LLamaCommunicator(),
                        CommunicatorType.OpenAI => new OpenAICommunicator(
                            new Uri(ViewModelManager.ViewModel.Communicator.ServerURL + "/chat/completions"),
                            ViewModelManager.ViewModel.Communicator.ApiKey,
                            ViewModelManager.ViewModel.Communicator.Model,
                            ViewModelManager.ViewModel.Communicator.ExpendedParams),
                        _ => throw ExceptionThrower.InvalidCommunicator,
                    };
                    TranslateData.GetUntranslatedData();
                    _history.Clear();
                    LoadHistory();
                    Translate();
                    //保存文件
                    SaveFiles();

                    int translateFailedTimes = ViewModelManager.ViewModel.SetView_ViewModel.TranslateFailedAgain ? ViewModelManager.ViewModel.SetView_ViewModel.TranslateFailedTimes : 0;
                    int i = 0;
                    while (true)
                    {
                        if (TryMergeData())
                        {
                            TranslateSuccessful();
                            break;
                        }
                        else
                        {
                            if (i >= translateFailedTimes)
                            {
                                //等待合并
                                TranslateNeedMerge();
                                break;
                            }
                            else
                            {
                                ViewModelManager.WriteLine($"[{DateTime.Now:G}]开始第{i + 1}次重翻失败部分");
                                //重新翻译失败部分
                                TranslateData.ClearFailedData();
                                TranslateData.GetUntranslatedData();
                                Translate();
                                SaveFiles();
                            }
                        }

                        i++;
                    }
                }
                catch (FileSaveException err)
                {
                    TranslateBreaked(err.Message, false, true);
                }
                catch (KnownException err)
                {
                    try
                    {
                        TranslateBreaked(err.Message, true, true);
                    }
                    catch (FileSaveException err2)
                    {
                        TranslateBreaked($"{err.Message} {err2.Message}", false, true);
                    }
                }
                catch (Exception err)
                {
                    string errorMsg;
                    if (string.IsNullOrWhiteSpace(err.ToString()))
                        errorMsg = "未知错误:" + err.InnerException;
                    else
                        errorMsg = "未知错误:" + err;
                    try
                    {
                        TranslateBreaked(errorMsg, true, false);
                    }
                    catch (FileSaveException err2)
                    {
                        //保存临时文件失败，不保存翻译文件暂停
                        TranslateBreaked(errorMsg, false, false);
                    }
                }
            });
        }

        /// <summary>
        /// 暂停翻译
        /// </summary>
        /// <returns>暂停翻译线程</returns>
        public void Pause()
        {
            _cts.Cancel();
            //通知停止线程
            _communicator?.Cancel();
            //等待线程停止
            _task?.Wait();

        }

        /// <summary>
        /// 翻译抽象方法，子类继承并实现翻译方式
        /// </summary>
        internal abstract void Translate();

        /// <summary>
        /// 合并翻译文件抽象方法，子类继承并实现合并流程
        /// </summary>
        private bool TryMergeData()
        {
            if (FailedDataCount != 0)
                return false;
            MergeData();
            return true;
        }

        internal abstract void MergeData();

        /// <summary>
        /// 完成翻译后保存文件并销毁Http连接
        /// </summary>
        void TranslateSuccessful()
        {
            _cts.Dispose();
            _communicator?.Dispose();
            _task = null;
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]翻译完成");
            TrigerStopedEvent(TranslateStopEventArgs.CreateSucess());
        }

        /// <summary>
        /// 完成翻译后保存文件并销毁Http连接
        /// </summary>
        void TranslateNeedMerge()
        {
            _cts.Dispose();
            _communicator?.Dispose();
            _task = null;
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]翻译完成，但存在翻译失败的文件，请手动翻译后合并");
            TrigerStopedEvent(TranslateStopEventArgs.CreateNeedMerge());
        }

        void TranslateBreaked(string error, bool save = true, bool isKnownError = true)
        {
            //保存文件
            if (save)
                SaveFiles();

            _cts.Dispose();
            _communicator?.Dispose();
            _task = null;

            ViewModelManager.WriteLine($"[{DateTime.Now:G}]{error} 翻译暂停");
            if (isKnownError)
                TrigerStopedEvent(TranslateStopEventArgs.CreatePause($"{error} 翻译暂停"));
            else
                TrigerStopedEvent(TranslateStopEventArgs.CreatePause("发生未知错误，翻译暂停\r\n请打开主窗口查看日志查看详细错误信息，并联系开发者解决"));
        }

        internal abstract void SaveSuccessfulFile();
        internal abstract void SaveFailedFile();
        void SaveFiles()
        {
            SaveSuccessfulFile();
            SaveFailedFile();
        }

        /// <summary>
        /// 翻译结果校验（内置过短检测 + .csx 脚本校验）
        /// </summary>
        /// <param name="source">原始数据</param>
        /// <param name="translated">翻译后数据</param>
        /// <param name="error">校验不通过原因</param>
        /// <returns>校验是否通过</returns>
        internal bool Verification(string source, string translated, out string error)
        {
            // 内置校验：输出过短（原文 > 100 字符且译文少于 25%，短文本语言间天然压缩不适用）
            if (source.Length > 100 && translated.Length < source.Length * 0.25)
            {
                error = "返回结果过短，怀疑未完整翻译或拒绝回答";
                return false;
            }

            // .csx 脚本校验
            _verificationScriptInput.Untranslated = source;
            _verificationScriptInput.Translated = translated;
            (bool, string) result = _verification.RunAsync(_verificationScriptInput).Result.ReturnValue;
            error = result.Item2;
            return result.Item1;
        }
        /// <summary>
        /// 加载历史记录
        /// </summary>
        internal abstract void LoadHistory();
        /// <summary>
        /// 添加历史上下文
        /// </summary>
        /// <param name="source">原始数据</param>
        /// <param name="translated">翻译后数据</param>
        internal void AddHistory(string source, string translated)
        {
            if (_translationTask.TemplateConfigParams.HistoryCount > 0)
            {
                if (_history.Count >= _translationTask.TemplateConfigParams.HistoryCount * 2)
                {
                    _history.Dequeue();
                    _history.Dequeue();
                }
                _history.Enqueue(new("user", $"这里是你需要翻译的文本：{source}"));
                _history.Enqueue(new("assistant", translated));
            }
        }

        static string[] escapeChars = ["\r\n", "\n", "\t"];

        /// <summary>
        /// 按句子边界拆分文本——
        /// ① 在 maxLen 范围内向前找第一个句点（跳过段长 < 20 的；分钟），段长 ≤ maxLen；
        /// ② 找不到（单句超长）时向 start+maxLen 后找最后一个句点（最多 +200），保证句子完整；
        /// ③ 仍找不到则硬切。
        /// </summary>
        internal static List<string> SplitBySentence(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
                return new List<string> { text };

            // 句子结尾标点（中日英通用）
            char[] sentenceEndings = { '。', '！', '？', '…', '．', '.', '!', '?', '\n', '\r', '；', ';' };

            const int forwardLookupLimit = 200;
            const int minSegmentChars = 100;
            var segments = new List<string>();
            int start = 0;

            while (start < text.Length)
            {
                // 剩余文本不超过限制，直接取到末尾
                if (start + maxLen >= text.Length)
                {
                    segments.Add(text[start..]);
                    break;
                }

                // ① 主策略：从 start 向前在 maxLen 内找第一个有效句点（段长 ≥ minSegmentChars）
                int end = -1;
                int searchPos = start;
                int remaining = maxLen;
                while (remaining > 0 && searchPos < start + maxLen)
                {
                    int found = text.IndexOfAny(sentenceEndings, searchPos, remaining);
                    if (found < 0)
                        break; // 没找到任何句点
                    if (found - start + 1 >= minSegmentChars)
                    {
                        end = found + 1; // 找到有效句点，包含标点
                        break;
                    }
                    // 跳过太短的段
                    int skip = found - searchPos + 1;
                    searchPos = found + 1;
                    remaining -= skip;
                }

                if (end < 0)
                {
                    // ② 兜底：单句超长或无有效句点，向 start+maxLen 后找最后一个句点（最多 +200）
                    int searchEnd = start + maxLen;
                    int backwardLimit = Math.Min(searchEnd + forwardLookupLimit, text.Length);
                    int backwardEnd = text.LastIndexOfAny(sentenceEndings, backwardLimit - 1, backwardLimit - start);
                    if (backwardEnd > start)
                        end = backwardEnd + 1; // 包含句尾标点
                    else
                        end = searchEnd; // ③ 仍找不到 → 硬切
                }

                segments.Add(text[start..end]);
                start = end;
            }

            return segments;
        }

        /// <summary>
        /// 多句合并翻译
        /// </summary>
        /// <param name="mergeValues">合并后的待翻译字符串列表</param>
        /// <returns>翻译完成的字符串列表</returns>
        internal string[] Translate_Mult(List<string> mergeValues, bool useHistory)
        {
            List<Dictionary<string, List<double>>> positions_list = new List<Dictionary<string, List<double>>>();
            List<string> processed_texts = new List<string>();
            foreach (var str in mergeValues)
            {
                (Dictionary<string, List<double>>, string) str_splites = str.CalculateNewlinePositions(escapeChars);
                positions_list.Add(str_splites.Item1);
                processed_texts.Add(str_splites.Item2);
            }

            string str_join = string.Join('\n', processed_texts);
            string str_result = TryTranslate(str_join, _example[^1].content! + "\n", useHistory, TryTranslateType.Mult);

            //如果返回结果的换行符数量不一致，调用逐句翻译模式
            string[] strs_result = str_result.Split('\n');
            if (strs_result.Length != mergeValues.Count)
                return null;

            string[] strs_result_reInsert = new string[strs_result.Length];
            for (int i = 0; i < strs_result.Length; i++)
                strs_result_reInsert[i] = strs_result[i].InsertNewlines(positions_list[i]);

            return strs_result_reInsert;
        }

        /// <summary>
        /// 单句翻译 — 如果文本超过 MaxSegmentLength 则自动拆分后逐段翻译再拼接
        /// </summary>
        /// <param name="value">待翻译的字符串</param>
        /// <param name="useHistory">使用历史上下文</param>
        /// <param name="retry">是否为重试（重试时 MaxSegmentLength 减半作为兜底）</param>
        /// <returns>翻译完成的字符串</returns>
        internal string Translate_Single(string value, bool useHistory, bool retry)
        {
            int maxLen = _translationTask.TemplateConfigParams.MaxSegmentLength;
            // 重试时强制拆分——避免 maxLen 仍大于文本长度导致拆分不触发
            if (retry)
                maxLen = Math.Max(Math.Min(maxLen / 2, value.Length * 3 / 4), 50);

            // 文本较短，保持原有单次翻译逻辑
            if (value.Length <= maxLen)
            {
                (Dictionary<string, List<double>>, string) result = value.CalculateNewlinePositions(escapeChars);
                Dictionary<string, List<double>> positions = result.Item1;
                string processed_texts = result.Item2;
                string str_result = TryTranslate(processed_texts, _example[^1].content!, useHistory, retry ? TryTranslateType.Retry : TryTranslateType.Single).InsertNewlines(positions);
                return str_result;
            }

            // 长文本：按句子边界拆分后逐段翻译
            var segments = SplitBySentence(value, maxLen);
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]{(retry ? "[重试]" : "")}文本长度{value.Length}超过限制{maxLen}，拆分为{segments.Count}段翻译");
            var translatedParts = new List<string>();
            string accumulated = "";
            for (int i = 0; i < segments.Count; i++)
            {
                string segment = segments[i];
                (Dictionary<string, List<double>>, string) segResult = segment.CalculateNewlinePositions(escapeChars);
                Dictionary<string, List<double>> segPositions = segResult.Item1;
                string segProcessed = segResult.Item2;
                string segTranslated = TryTranslate(segProcessed, _example[^1].content!, useHistory, retry ? TryTranslateType.Retry : TryTranslateType.Single, accumulated);
                string formatted = segTranslated.InsertNewlines(segPositions);
                accumulated += formatted;
                ViewModelManager.WriteLine($"  [{DateTime.Now:G}]第{i + 1}/{segments.Count}段完成，原文:{segment.Length}字符 → 译文:{segTranslated.Length}字符");
                translatedParts.Add(formatted);
            }
            string joined = string.Concat(translatedParts);
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]{(retry ? "[重试]" : "")}拆分翻译完成，合并后译文共{joined.Length}字符");
            return joined;
        }

        internal string Translate_NoResetNewline(string value, bool useHistory, TryTranslateType type)
        {
            int maxLen = _translationTask.TemplateConfigParams.MaxSegmentLength;
            if (type == TryTranslateType.Retry)
                maxLen = Math.Max(Math.Min(maxLen / 2, value.Length * 3 / 4), 50);

            if (value.Length <= maxLen)
                return TryTranslate(value, _example[^1].content!, useHistory, type);

            var segments = SplitBySentence(value, maxLen);
            bool isRetry = type == TryTranslateType.Retry;
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]{(isRetry ? "[重试]" : "")}文本长度{value.Length}超过限制{maxLen}，拆分为{segments.Count}段翻译");
            var translatedParts = new List<string>();
            string accumulated = "";
            for (int i = 0; i < segments.Count; i++)
            {
                string segment = segments[i];
                string segTranslated = TryTranslate(segment, _example[^1].content!, useHistory, type, accumulated);
                accumulated += segTranslated;
                ViewModelManager.WriteLine($"  [{DateTime.Now:G}]第{i + 1}/{segments.Count}段完成，原文:{segment.Length}字符 → 译文:{segTranslated.Length}字符");
                translatedParts.Add(segTranslated);
            }
            string joined = string.Concat(translatedParts);
            ViewModelManager.WriteLine($"[{DateTime.Now:G}]{(isRetry ? "[重试]" : "")}拆分翻译完成，合并后译文共{joined.Length}字符");
            return joined;
        }

        /// <summary>
        /// 进行翻译
        /// </summary>
        /// <param name="str">待翻译的字符串</param>
        /// <param name="isMult">是否是合并翻译</param>
        /// <param name="useHistory">使用历史上下文</param>
        /// <param name="temperature">温度</param>
        /// <param name="frequencyPenalty">频率惩罚</param>
        /// <returns>翻译完成的字符串</returns>
        /// <exception cref="KnownException">出现的已知错误</exception>
        internal string TryTranslate(string str, string prompt_with_text, bool useHistory, TryTranslateType type, string? accumulatedContext = null, int _splitDepth = 0)
        {
            ExampleDialogue[] headers = _example[..^1];
            var historyList = (useHistory ? _history.ToList() : new List<ExampleDialogue>());

            // 拆分翻译时传递前段累计译文作为上下文
            if (!string.IsNullOrEmpty(accumulatedContext))
            {
                historyList.Add(new ExampleDialogue("user", "前文翻译结果"));
                historyList.Add(new ExampleDialogue("assistant", accumulatedContext));
            }
            ExampleDialogue[] histories = historyList.ToArray();

            ConfigSave_TranslatePrams? param = GetTranslatePrams(type);
            if (param is null)
                throw new KnownException("未配置翻译参数！请先前往高级参数配置翻译所需的参数");

            PostDataBase postData = new PostDataBase();
            postData.temperature = param.Temperature;
            postData.frequency_penalty = param.FrequencyPenalty;
            postData.max_tokens = (int)param.MaxTokens;
            postData.top_p = param.TopP;
            postData.presence_penalty = param.PresencePenalty;
            postData.stop = param.Stops.ToArray();

            try
            {
                string str_result = _communicator!.Translate(postData, headers, histories, $"{prompt_with_text}{str}", out double speed);
                _translationTask.Speed = speed;
                return str_result;
            }
            catch (KnownException ex) when (ex.Message.StartsWith("EXCEED_CONTEXT|"))
            {
                const int MaxSplitDepth = 3;
                if (_splitDepth >= MaxSplitDepth || str.Length <= 1)
                    throw; // 已达最大拆分深度或文本无法再拆分，向上抛出

                // 解析 prompt token 和 context 上限
                int prompt = 0, ctx = 0;
                foreach (var segment in ex.Message.Split('|'))
                {
                    if (segment.StartsWith("prompt=")) int.TryParse(segment.AsSpan(7), out prompt);
                    else if (segment.StartsWith("ctx=")) int.TryParse(segment.AsSpan(4), out ctx);
                }

                if (prompt <= 0 || ctx <= 0)
                    throw; // 无法解析，按普通错误处理

                // 按 token 比例换算字符拆分上限（安全系数 0.85）
                double ratio = (double)ctx / prompt * 0.85;
                int maxChars = Math.Max(50, (int)(str.Length * ratio));

                ViewModelManager.WriteLine($"[{DateTime.Now:G}][自动拆分] 请求{prompt}tokens 超过上下文{ctx}tokens，将文本({str.Length}字符)按~{maxChars}字符/段拆分翻译");

                var segments = SplitBySentence(str, maxChars);
                var translatedParts = new List<string>();
                string accumulated = accumulatedContext ?? "";

                for (int i = 0; i < segments.Count; i++)
                {
                    string segTranslated = TryTranslate(segments[i], prompt_with_text, false, type, accumulated, _splitDepth + 1);
                    accumulated += segTranslated;
                    ViewModelManager.WriteLine($"  [{DateTime.Now:G}]拆分第{i + 1}/{segments.Count}段完成");
                    translatedParts.Add(segTranslated);
                }

                ViewModelManager.WriteLine($"[{DateTime.Now:G}][自动拆分] 全部{segments.Count}段翻译完成");
                return string.Concat(translatedParts);
            }
        }

        ConfigSave_TranslatePrams? GetTranslatePrams(TryTranslateType type)
        {
            ConfigSave_TranslatePrams? transParams = type switch
            {
                TryTranslateType.Single => _translationTask.TemplateConfigParams.TranslatePrams_FirstSingle,
                TryTranslateType.Mult => _translationTask.TemplateConfigParams.TranslatePrams_FirstMult,
                TryTranslateType.Retry => _translationTask.TemplateConfigParams.TranslatePrams_Retry,
                _ => null,
            };

            return transParams;
        }
    }
}
