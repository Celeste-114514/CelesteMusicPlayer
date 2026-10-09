// MainWindow.DspProfiles.cs
// 听音方案页：把「当前整套 DSP 链路设置」存成命名方案，一键保存 / 应用 / 更新 / 删除。
//
// 边界（不新造参数格式、不新开下发通道）：
//   捕获 = 既有各 store 当前值的深拷贝（结构见 DspProfileState.cs 的 DspProfile）；
//   应用 = 快照写回各 store → 面板从盘重载（与启动期同一条 LoadAudioFxUiFromStore 路径）
//          → 走既有 ApplyDspToEngine / PushDspRackToEngine / ApplyReplayGainToEngine 下发引擎，
//          和用户手动逐个模块改设置完全同链路（含实时生效、含未播放时只存状态）。
//
// 「当前设置是否还属于某方案」不靠每个 handler 上报，而是拿方案内容指纹和当前设置比对
// （DspProfileFingerprint）：任何入口改了 DSP 设置都能被发现，不依赖各模块是否记得喊一声。
// 发现偏离就地降级为「手动设置」，并把 dsp-profiles.json 里的 ActiveProfile 清掉（只写一次）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>当前生效方案名（空 = 手动设置）。盘上 ActiveProfile 的内存镜像，见 RefreshDspProfileUi。</summary>
        private string _dspProfileActiveName = string.Empty;

        /// <summary>刚脱离的那份方案名：只用于页面提示「已从方案 X 改出去」，不落盘。</summary>
        private string _dspProfileLeftName = string.Empty;

        /// <summary>生效方案的内容指纹（自盘上快照算出）。空 = 当前无生效方案。</summary>
        private string _dspProfileRefKey = string.Empty;

        /// <summary>列表当前展示的方案，下标与 DspProfileList 的行一一对应。</summary>
        private readonly List<DspProfile> _dspProfileRows = new();

        // ─────────────────────────────────────────────────────────────
        // 刷新：store → 页面
        // ─────────────────────────────────────────────────────────────

        /// <summary>把 dsp-profiles.json 的现状灌进方案页（列表 / 状态行 / 按钮可用性 / 徽章）。</summary>
        private void RefreshDspProfileUi()
        {
            try
            {
                DspProfileBook book = DspProfileStore.Load();
                _dspProfileActiveName = book.ActiveProfile ?? string.Empty;

                _dspProfileRows.Clear();
                var rows = new List<string>(book.Profiles.Count);
                DspProfile? active = null;
                foreach (DspProfile p in book.Profiles)
                {
                    _dspProfileRows.Add(p);
                    if (string.Equals(p.Name, _dspProfileActiveName, StringComparison.OrdinalIgnoreCase))
                    {
                        active = p;
                    }
                }

                // 生效方案的内容指纹：用来判断"应用之后又被改过没有"
                _dspProfileRefKey = active == null ? string.Empty : DspProfileFingerprint(active);

                // 进页面 / 保存后先判定漂移，再生成行文本（行的 ● 标记与「已存 N 份」都要用最终状态）
                EnsureDspProfileFresh();

                for (int i = 0; i < _dspProfileRows.Count; i++)
                {
                    rows.Add(FormatDspProfileRow(_dspProfileRows[i], i));
                }

                if (DspProfileList != null)
                {
                    DspProfileList.ItemsSource = rows;
                    int restore = -1;
                    for (int i = 0; i < _dspProfileRows.Count; i++)
                    {
                        if (string.Equals(_dspProfileRows[i].Name, _dspProfileActiveName, StringComparison.OrdinalIgnoreCase))
                        {
                            restore = i;
                            break;
                        }
                    }

                    // 没有生效方案时不预选（避免"看起来选中了但其实没应用"）；有则选中它
                    DspProfileList.SelectedIndex = restore;
                }

                UpdateDspProfileTexts();
                UpdateDspProfileButtons();
                ApplyDspProfileBadge();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
        }

        /// <summary>列表单行文本：生效方案带 ● 前缀并标注「当前」。</summary>
        private string FormatDspProfileRow(DspProfile p, int index)
        {
            bool active = string.Equals(p.Name, _dspProfileActiveName, StringComparison.OrdinalIgnoreCase);
            string stamp = FormatDspProfileStamp(p.ModifiedUtc, p.CreatedUtc);
            string head = active ? "● " : (index + 1).ToString(CultureInfo.InvariantCulture) + ". ";
            return stamp.Length == 0
                ? head + p.Name + (active ? "　（当前）" : string.Empty)
                : head + p.Name + "　·　" + stamp + (active ? "　（当前）" : string.Empty);
        }

        /// <summary>方案时间戳显示（本地时间，MM-dd HH:mm）；两个字段都空/坏则返回空串。</summary>
        private static string FormatDspProfileStamp(string modifiedUtc, string createdUtc)
        {
            foreach (string raw in new[] { modifiedUtc, createdUtc })
            {
                if (!string.IsNullOrWhiteSpace(raw)
                    && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t))
                {
                    return t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }

            return string.Empty;
        }

        /// <summary>刷新方案页内的状态行与提示文字（不含列表本身）。</summary>
        private void UpdateDspProfileTexts()
        {
            if (DspProfileStatusText != null)
            {
                int count = _dspProfileRows.Count;
                string now = string.IsNullOrEmpty(_dspProfileActiveName)
                    ? (string.IsNullOrEmpty(_dspProfileLeftName)
                        ? "当前：手动设置（未套用任何方案）"
                        : "当前：手动设置（已从方案「" + _dspProfileLeftName + "」改出去）")
                    : "当前：方案「" + _dspProfileActiveName + "」正在生效";
                DspProfileStatusText.Text = now + "　｜　已存 " + count + " 份方案";
            }

            if (DspProfileHintText == null)
            {
                return;
            }

            if (_dspProfileRows.Count == 0)
            {
                DspProfileHintText.Text = "还没有方案。先把各模块调到你满意的声音，再在上面起个名字点「保存为方案」。";
            }
            else if (!string.IsNullOrEmpty(_dspProfileActiveName))
            {
                DspProfileHintText.Text = "当前设置来自方案「" + _dspProfileActiveName
                    + "」。应用后再改任一模块就会自动脱离该方案（方案本身不变，要留住改动点「更新为当前设置」）。";
            }
            else if (!string.IsNullOrEmpty(_dspProfileLeftName))
            {
                DspProfileHintText.Text = "当前是手动设置：已从方案「" + _dspProfileLeftName
                    + "」改出去。想留住这次改动，选中那份方案点「更新为当前设置」，或另存为新方案。";
            }
            else
            {
                DspProfileHintText.Text = "当前是手动设置。选中一份方案点「应用方案」即整套切过去。";
            }
        }

        /// <summary>应用 / 更新 / 删除按钮只在选中某一行时可用。</summary>
        private void UpdateDspProfileButtons()
        {
            bool has = SelectedDspProfile() != null;
            if (DspProfileApplyButton != null)
            {
                DspProfileApplyButton.IsEnabled = has;
            }

            if (DspProfileUpdateButton != null)
            {
                DspProfileUpdateButton.IsEnabled = has;
            }

            if (DspProfileDeleteButton != null)
            {
                DspProfileDeleteButton.IsEnabled = has;
            }
        }

        /// <summary>方案页徽章渲染：显示"当前设置来自哪份方案"。它是状态而不是处理模块，
        /// 所以不受 DSP 总旁路影响，也不显示「已旁路」。</summary>
        private void ApplyDspProfileBadge()
        {
            if (DspBadgeProfiles == null || DspBadgeProfilesText == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(_dspProfileActiveName))
            {
                SetDspBadgeState(DspBadgeProfiles, DspBadgeProfilesText, "off", "手动设置");
            }
            else
            {
                SetDspBadgeState(DspBadgeProfiles, DspBadgeProfilesText, "on", "方案「" + _dspProfileActiveName + "」");
            }
        }

        /// <summary>
        /// 生效方案与当前设置比对：不一致 = 用户已经在别处改过，就地降级为「手动设置」。
        /// 每个刷新入口（导航圆点 / 页面徽章 / 进页面）先过这一道，判断是懒执行但最终一定成立。
        /// 盘上的 ActiveProfile 只清这一次（清完 _dspProfileActiveName 为空，不再进本方法）。
        /// </summary>
        private void EnsureDspProfileFresh()
        {
            if (string.IsNullOrEmpty(_dspProfileActiveName) || !DspProfileDrifted())
            {
                return;
            }

            _dspProfileLeftName = _dspProfileActiveName;
            _dspProfileActiveName = string.Empty;
            _dspProfileRefKey = string.Empty;
            DspProfileStore.MarkCustom();
            UpdateDspProfileTexts();
        }

        /// <summary>当前设置是否已偏离生效方案。无生效方案 / 指纹未就绪时返回 false。</summary>
        private bool DspProfileDrifted()
        {
            if (string.IsNullOrEmpty(_dspProfileRefKey))
            {
                return false;
            }

            try
            {
                return !string.Equals(_dspProfileRefKey, DspProfileFingerprint(CaptureCurrentDspProfile()), StringComparison.Ordinal);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 捕获：当前设置 → 一份方案快照
        // ─────────────────────────────────────────────────────────────

        /// <summary>抓当前整套 DSP 设置为一份方案（各 store 当前值的深拷贝，不引用任何缓存对象）。</summary>
        private DspProfile CaptureCurrentDspProfile()
        {
            DspExtraState extra = DspExtraStore.Load();
            SimpleEqState simple = SimpleEqStore.Load();
            AppSettingsState settings = AppSettingsStore.Load();

            // EQ 用面板内存态（A/B 试听预览期间盘上还是旧曲线，界面看到的才算"当前设置"）
            EqCurveState eq = _audioFxEq != null ? _audioFxEq.Clone() : EqCurveStore.Load();

            return new DspProfile
            {
                Rack = DspRackStore.Load().Clone(),
                Eq = eq,
                Extra = new DspExtraState
                {
                    ChannelBalance = extra.ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
                    Safety = extra.Safety?.Clone() ?? DspSafetyState.Default()
                },
                ReplayGain = ReplayGainStore.Load().Clone(),
                Room = RoomCorrectionStore.Load().Clone(),
                SimpleEq = new SimpleEqState
                {
                    Bass = simple.Bass,
                    Vocal = simple.Vocal,
                    Air = simple.Air,
                    Warm = simple.Warm
                },
                SrcTargetHz = settings.SrcTargetHz,
                SrcQuality = string.IsNullOrWhiteSpace(settings.SrcQuality) ? "balanced" : settings.SrcQuality,
                SrcDither = string.IsNullOrWhiteSpace(settings.SrcDither) ? "off" : settings.SrcDither
            };
        }

        /// <summary>
        /// 方案内容指纹：只取"用户可调的设置值"，用于判断当前设置是否还等于某份方案。
        /// 刻意不含 IR 的 taps / 采样率 / 声道数这类"加载后回写"字段 —— 它们会被播放过程改写，
        /// 放进来会让刚应用完的方案立刻被误判成"已改动"。
        /// </summary>
        private static string DspProfileFingerprint(DspProfile p)
        {
            var b = new StringBuilder(1024);
            void Num(double v) => b.Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append('|');
            void Flag(bool v) => b.Append(v ? '1' : '0').Append('|');

            // 机架：顺序 + 压缩器 + 立体声场 + 声道矩阵
            RackState? rack = p.Rack;
            if (rack?.RackOrder != null)
            {
                foreach (int id in rack.RackOrder)
                {
                    b.Append(id).Append(',');
                }
            }

            b.Append('|');
            if (rack?.Compressor is { } c)
            {
                Flag(c.Enabled); Num(c.ThresholdDb); Num(c.Ratio); Num(c.AttackMs);
                Num(c.ReleaseMs); Num(c.KneeDb); Num(c.MakeupDb); Num(c.Mix);
            }

            if (rack?.StereoField is { } f)
            {
                Flag(f.Enabled); Num(f.Width); Num(f.CenterGainDb); Num(f.SideGainDb);
            }

            if (rack?.Matrix is { } m)
            {
                Flag(m.Enabled); Num(m.LeftToLeft); Num(m.RightToLeft); Num(m.LeftToRight); Num(m.RightToRight);
            }

            // EQ 曲线（含耳机校正写进来的那条）
            if (p.Eq is { } eq)
            {
                Flag(eq.Enabled); Num(eq.PreampDb); b.Append(eq.PresetId).Append('|');
                foreach (EqBand band in eq.Bands)
                {
                    Flag(band.Enabled); Num(band.FrequencyHz); Num(band.GainDb); Num(band.Q);
                    b.Append((int)band.FilterType).Append('|');
                }
            }

            // 声道工具 + 输出安全
            if (p.Extra?.ChannelBalance is { } ch)
            {
                Flag(ch.Enabled); Num(ch.Balance); Num(ch.LeftGainDb); Num(ch.RightGainDb);
                Flag(ch.InvertLeft); Flag(ch.InvertRight); Flag(ch.SwapChannels);
                b.Append(ch.MonoMode).Append('|');
                Num(ch.LeftDelayMs); Num(ch.RightDelayMs);
                Flag(ch.CrossfeedEnabled); b.Append(ch.CrossfeedLevel).Append('|'); Num(ch.CrossfeedCutoffHz);
            }

            if (p.Extra?.Safety is { } safety)
            {
                Num(safety.HeadroomDb); Flag(safety.EnableLimiter);
            }

            // ReplayGain
            if (p.ReplayGain is { } rg)
            {
                b.Append((int)rg.Mode).Append('|'); Num(rg.PreampDb); Flag(rg.PreventClipping);
            }

            // 房间校正：只比用户设定 + IR 路径（长度/采样率是加载后回写的，见方法注释）
            if (p.Room is { } room)
            {
                Flag(room.Enabled); b.Append(room.IrPath).Append('|'); Num(room.GainDb); Num(room.TrimDb);
            }

            // 简单模式四滑杆
            if (p.SimpleEq is { } se)
            {
                Num(se.Bass); Num(se.Vocal); Num(se.Air); Num(se.Warm);
            }

            // 升频三件套
            b.Append(p.SrcTargetHz).Append('|').Append(p.SrcQuality).Append('|').Append(p.SrcDither);
            return b.ToString();
        }

        // ─────────────────────────────────────────────────────────────
        // 应用：一份方案快照 → store → 面板 → 引擎
        // ─────────────────────────────────────────────────────────────

        /// <summary>把一份方案整套切过去：写回各 store → 重载面板 → 走既有下发入口推引擎。</summary>
        private void ApplyDspProfile(DspProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            try
            {
                // 0) 逐段取本地非空引用：dsp-profiles.json 被手工改坏（某段写成 null）时也不炸，
                //    缺的那段按默认值落回 store，其余照常切过去。
                DspExtraState extra = profile.Extra ?? new DspExtraState();
                SimpleEqState simple = profile.SimpleEq ?? new SimpleEqState();
                EqCurveState eq = profile.Eq ?? EqCurveState.Default();
                RackState rack = profile.Rack ?? new RackState();
                ReplayGainState rg = profile.ReplayGain ?? new ReplayGainState();
                RoomCorrectionState room = profile.Room ?? new RoomCorrectionState();

                // 1) 快照写回各 store（落的就是用户手动改设置时的那几个文件）
                DspRackStore.Save(rack.Clone());
                EqCurveStore.Save(eq.Clone());
                DspExtraStore.Save(new DspExtraState
                {
                    ChannelBalance = extra.ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
                    Safety = extra.Safety?.Clone() ?? DspSafetyState.Default()
                });
                ReplayGainStore.Save(rg.Clone());
                RoomCorrectionStore.Save(room.Clone());
                SimpleEqStore.Save(new SimpleEqState
                {
                    Bass = simple.Bass,
                    Vocal = simple.Vocal,
                    Air = simple.Air,
                    Warm = simple.Warm
                });

                AppSettingsState settings = AppSettingsStore.Load();
                settings.SrcTargetHz = profile.SrcTargetHz;
                settings.SrcQuality = profile.SrcQuality;
                settings.SrcDither = profile.SrcDither;
                AppSettingsStore.Save(settings);

                // 生效方案先记上（含盘），再重载面板：重载期间的状态刷新就已经是"属于这份方案"了
                _dspProfileActiveName = profile.Name;
                _dspProfileLeftName = string.Empty;
                DspProfileStore.SetActive(profile.Name);

                // 2) 面板从盘重载（启动期同一条路径：面板始终以 store 为唯一真值）
                ReloadDspPanelFromStores();

                // 耳机校正标记按当前 EQ 曲线 id 直读（_opraApplied 计算属性），无需在此赋值

                // 3) 走既有下发入口推进引擎（各自内部持久化 + SetXxx，与手动改动同链路）
                ApplyDspToEngine();
                PushDspRackToEngine();
                ApplyReplayGainToEngine();

                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                UpdateSignalChainDisplay();
                StartupLog.Write("[听音方案] 已应用「" + profile.Name + "」 机架=" + rack.RackOrder.Length
                    + " EQ段=" + eq.Bands.Count + " 升频=" + profile.SrcTargetHz + "Hz");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
        }

        /// <summary>
        /// 重载面板 UI（EQ / 声道 / 安全 / RG / 机架 / 升频下拉），期间禁掉一切下发：
        /// 控件赋值会就地触发 Toggled / ValueChanged，若此时 _audioFxPanelReady 仍为 true，
        /// 半载状态的回写会污染 store。这里显式关掉两个守卫，重载完再由调用方统一下发。
        /// </summary>
        private void ReloadDspPanelFromStores()
        {
            bool prevReady = _audioFxPanelReady;

            // _audioFxPanelReady 是 ApplyDspToEngine / ApplyReplayGainToEngine 的第一道守卫；
            // _audioFxLoading 挡住各控件 handler 的回写。两个都要关：LoadAudioFxUiFromStore 内部
            // 会在中途把 _audioFxPanelReady 置回 true。
            _audioFxPanelReady = false;
            _audioFxLoading = true;
            try
            {
                LoadAudioFxUiFromStore();
                LoadDspRackUi();
                InitializeSrcUi();
            }
            finally
            {
                _audioFxLoading = false;
                // LoadAudioFxUiFromStore 内部会把面板标记为已就绪；这里保证只要之前就绪，之后仍然就绪。
                _audioFxPanelReady = prevReady || _audioFxPanelReady;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 页面事件：保存 / 应用 / 更新 / 删除
        // ─────────────────────────────────────────────────────────────

        private void DspProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDspProfileButtons();
        }

        /// <summary>「保存为方案」：把当前整套设置存成新方案；同名则覆盖更新。</summary>
        private void DspProfileSave_Click(object sender, RoutedEventArgs e)
        {
            SaveDspProfileCore(DspProfileNameBox?.Text ?? string.Empty);
        }

        /// <summary>保存方案核心（网页「音效处理」页直接调，name 由调用方给）。</summary>
        internal void SaveDspProfileCore(string rawName)
        {
            try
            {
                string name = (rawName ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    name = NextDspProfileName();
                }

                if (name.Length > 40)
                {
                    name = name[..40];
                }

                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                DspProfile fresh = CaptureCurrentDspProfile();
                fresh.Name = name;
                fresh.ModifiedUtc = now;

                bool replaced = false;
                DspProfileStore.Update(book =>
                {
                    int i = book.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        fresh.CreatedUtc = string.IsNullOrWhiteSpace(book.Profiles[i].CreatedUtc) ? now : book.Profiles[i].CreatedUtc;
                        book.Profiles[i] = fresh;
                        replaced = true;
                    }
                    else
                    {
                        fresh.CreatedUtc = now;
                        book.Profiles.Add(fresh);
                    }

                    book.ActiveProfile = name;
                });

                _dspProfileActiveName = name;
                _dspProfileLeftName = string.Empty;
                if (DspProfileNameBox != null)
                {
                    DspProfileNameBox.Text = string.Empty;
                }

                if (DspProfileSaveHintText != null)
                {
                    DspProfileSaveHintText.Text = replaced
                        ? "已用当前设置覆盖同名方案「" + name + "」。"
                        : "已保存方案「" + name + "」，当前设置现在属于它。";
                }

                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                StartupLog.Write("[听音方案] 已保存「" + name + "」(覆盖=" + replaced + ") EQ段=" + (fresh.Eq?.Bands?.Count ?? 0));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
        }

        /// <summary>「应用方案」：整套切到选中方案。</summary>
        private void DspProfileApply_Click(object sender, RoutedEventArgs e)
        {
            ApplySelectedDspProfileCore();
        }

        /// <summary>应用选中方案核心（网页「音效处理」页直接调）。</summary>
        internal void ApplySelectedDspProfileCore()
        {
            DspProfile? row = SelectedDspProfile();
            if (row == null)
            {
                return;
            }

            ApplyDspProfile(row);
        }

        /// <summary>「更新为当前设置」：把选中方案的内容换成当前设置（名字与创建时间保留）。</summary>
        private void DspProfileUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateSelectedDspProfileCore();
        }

        /// <summary>更新选中方案为当前设置核心（网页「音效处理」页直接调）。</summary>
        internal void UpdateSelectedDspProfileCore()
        {
            try
            {
                DspProfile? row = SelectedDspProfile();
                if (row == null)
                {
                    return;
                }

                string name = row.Name;
                string created = row.CreatedUtc;
                DspProfile fresh = CaptureCurrentDspProfile();
                fresh.Name = name;
                fresh.CreatedUtc = created;
                fresh.ModifiedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                DspProfileStore.Update(book =>
                {
                    int i = book.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        fresh.CreatedUtc = string.IsNullOrWhiteSpace(book.Profiles[i].CreatedUtc) ? fresh.CreatedUtc : book.Profiles[i].CreatedUtc;
                        book.Profiles[i] = fresh;
                    }
                    else
                    {
                        book.Profiles.Add(fresh);
                    }

                    book.ActiveProfile = name;
                });

                _dspProfileActiveName = name;
                _dspProfileLeftName = string.Empty;
                if (DspProfileSaveHintText != null)
                {
                    DspProfileSaveHintText.Text = "方案「" + name + "」已更新为当前设置。";
                }

                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                StartupLog.Write("[听音方案] 已更新「" + name + "」");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
        }

        /// <summary>「删除方案」：只删快照，不动当前各模块设置（声音不会因此改变）。</summary>
        private void DspProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedDspProfileCore();
        }

        /// <summary>删除选中方案核心（网页「音效处理」页直接调）。</summary>
        internal void DeleteSelectedDspProfileCore()
        {
            try
            {
                DspProfile? row = SelectedDspProfile();
                if (row == null)
                {
                    return;
                }

                string name = row.Name;
                bool wasActive = string.Equals(_dspProfileActiveName, name, StringComparison.OrdinalIgnoreCase);

                DspProfileStore.Update(book =>
                {
                    book.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(book.ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
                    {
                        book.ActiveProfile = string.Empty;
                    }
                });

                if (wasActive)
                {
                    _dspProfileActiveName = string.Empty;
                    _dspProfileLeftName = string.Empty;
                    _dspProfileRefKey = string.Empty;
                }

                if (DspProfileSaveHintText != null)
                {
                    DspProfileSaveHintText.Text = "已删除方案「" + name + "」。当前各模块设置原样保留，声音不会变。";
                }

                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                StartupLog.Write("[听音方案] 已删除「" + name + "」");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 小工具
        // ─────────────────────────────────────────────────────────────

        /// <summary>列表选中项对应的方案；未选中返回 null。</summary>
        private DspProfile? SelectedDspProfile()
        {
            int i = DspProfileList?.SelectedIndex ?? -1;
            return i >= 0 && i < _dspProfileRows.Count ? _dspProfileRows[i] : null;
        }

        /// <summary>名字留空时的默认名：取第一个没被占用的「方案 N」。</summary>
        private string NextDspProfileName()
        {
            DspProfileBook book = DspProfileStore.Load();
            for (int n = 1; n <= 99; n++)
            {
                string candidate = "方案 " + n.ToString(CultureInfo.InvariantCulture);
                if (!book.Profiles.Exists(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }

            return "方案 " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
