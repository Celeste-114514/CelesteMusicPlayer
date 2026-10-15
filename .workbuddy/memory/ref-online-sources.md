# 在线源：定局与商用条款（长期有效）

## 已删 / 已接入
- 国内平台（网易云 / QQ / 酷狗 / 酷我 / iTunes 搜歌）因**合规整改删除**
  （提交 f058aeb / 1328c77 / 27ec416），**不可恢复**，勿暗示"配 key 就能恢复"。
- 海外源已接入（非被删平台）：MusicBrainz + iTunes（免 key）、Discogs / Last.fm（需 Key）、
  fanart.tv（按 MBID 取 HD 封面）。
- ⚠ 仍有酷狗残留（2026-10-15 实查，多为注释/字符串）：MainWindow.ArtistDetail.cs /
  ArtistAvatarSearchWindow / ArtistAvatarEditPanelControl / LyricsLoader.cs / OnlineMusicApi.cs /
  MainWindow.Lyrics.cs、Library.cs、Library2.cs、WebMain.cs / SettingsPanelControl.xaml.cs /
  SettingsWindow.xaml(.cs) / AppSettingsStore.cs 仍含 酷狗|网易|QQ 字样。
  艺术家详情页概览仍在走酷狗 = 违规，待办 A4。

## 商用条款（2026-10-15 查官方 ToS）
- **Last.fm**：ToS 3.1/3.2 明确**仅限非商业用途**，商用须书面协议（partners@last.fm），
  违约可立即停 key + 追偿；官方保留未来分成权。用户自填 key **不改变**"应用是否商用"的判定
  → "免费版带 Last.fm、付费 DLC 不带"**站不住**。
- **Discogs**：商用一般允许，但 ① Restricted Data 禁商用 ② **禁止对"集成 Discogs 数据的那部分功能"
  单独收费**（除非书面许可）③ 必须署名："本应用使用 Discogs API，但未被 Discogs 赞助或认可"
  + 数据旁 "Data provided by Discogs" 带**可传 SEO 权重**的链接（不能 nofollow）。署名现在就该加。
- **fanart.tv**：API 文档标 CC BY 3.0（署名即可商用）；真正风险在**图片本身版权**（摄影者/厂牌），
  不在 API。署名待加。
- **GPL-3.0 + Steam 商业化**：GPL 允许卖但须给源码；**Steamworks SDK 不能链接进 GPL 程序**，
  上 Steam 卖须改许可或做成独立进程。商业化前必须先定。
- 建议：给在线源加"商用开关/编译条件"，商业化时一键下架 Last.fm / Discogs / fanart.tv，只留 MB+CAA。
