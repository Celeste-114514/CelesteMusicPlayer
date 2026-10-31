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
        /// <summary>「当前设置属于哪份方案」的判定住在 <see cref="DspProfileCoordinator"/>（见 DspStateHost.cs），
        /// 这里只留页面的行缓存。</summary>
        private readonly List<DspProfile> _dspProfileRows = new();




        /// <summary>
        /// 生效方案与当前设置比对：不一致 = 用户已经在别处改过，就地降级为「手动设置」。
        /// 每个刷新入口（导航圆点 / 页面徽章 / 进页面）先过这一道，判断是懒执行但最终一定成立。
        /// 盘上的 ActiveProfile 只清这一次（清完 _dspProfileActiveName 为空，不再进本方法）。
        /// </summary>
        private void EnsureDspProfileFresh()
        {
            // 判定与降级都由协调器做；宿主只负责它说"已经偏离"之后的落盘与刷新
            if (!ProfileCoordinator.Refresh(CaptureCurrentDspProfile))
            {
                return;
            }

            DspProfileStore.MarkCustom();
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
            EqCurveState eq = EqState.Current != null ? EqState.Current.Clone() : EqCurveStore.Load();

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
                ProfileCoordinator.Apply(profile.Name, DspProfileCoordinator.Fingerprint(profile));
                DspProfileStore.SetActive(profile.Name);

                // 2) 面板从盘重载（启动期同一条路径：面板始终以 store 为唯一真值）
                ReloadDspPanelFromStores();

                // 耳机校正标记按当前 EQ 曲线 id 直读（_opraApplied 计算属性），无需在此赋值

                // 3) 走既有下发入口推进引擎（各自内部持久化 + SetXxx，与手动改动同链路）
                ApplyDspToEngine();
                PushDspRackToEngine();
                ApplyReplayGainToEngine();

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
                // 房间校正不进 dsp-rack.json，参数模型要从它自己的存档重新装载（否则方案切过去后模型是旧值）
                LoadRoomCorrectionIntoPanel();
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

                ProfileCoordinator.Apply(name, DspProfileCoordinator.Fingerprint(fresh));


                UpdateDspNavIndicators();
                StartupLog.Write("[听音方案] 已保存「" + name + "」(覆盖=" + replaced + ") EQ段=" + (fresh.Eq?.Bands?.Count ?? 0));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
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

                ProfileCoordinator.Apply(name, DspProfileCoordinator.Fingerprint(fresh));

                UpdateDspNavIndicators();
                StartupLog.Write("[听音方案] 已更新「" + name + "」");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspProfiles.cs", caught);
            }
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
                bool wasActive = string.Equals(ProfileCoordinator.ActiveName, name, StringComparison.OrdinalIgnoreCase);

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
                    ProfileCoordinator.Clear();
                }


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
