package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"net/http"
	"os"
	"strings"
	"time"

	botconf "github.com/liuran001/MusicBot-Go/bot/config"
	logpkg "github.com/liuran001/MusicBot-Go/bot/logger"
	"github.com/liuran001/MusicBot-Go/bot/platform"
	pluginregistry "github.com/liuran001/MusicBot-Go/bot/platform/plugins"
	_ "github.com/liuran001/MusicBot-Go/plugins/all"
)

var mgr *platform.DefaultManager

func main() {
	configPath := flag.String("c", "config.ini", "配置文件路径")
	addr := flag.String("addr", "0.0.0.0:21010", "监听地址:端口")
	flag.Parse()

	conf, err := botconf.Load(*configPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "加载配置失败:", err)
		os.Exit(1)
	}
	log, err := logpkg.New(conf.GetString("LogLevel"), conf.GetString("LogFormat"), false)
	if err != nil {
		log, _ = logpkg.New("info", "text", false)
	}

	mgr = buildManager(conf, log)
	log.Info("streamingserver ready", "platform", strings.Join(mgr.List(), ","), "addr", *addr)

	mux := http.NewServeMux()
	mux.HandleFunc("/api/ping", handlePing)
	mux.HandleFunc("/api/platforms", handlePlatforms)
	mux.HandleFunc("/api/search", handleSearch)
	mux.HandleFunc("/api/lyric", handleLyric)
	mux.HandleFunc("/api/download", handleDownload)

	srv := &http.Server{Addr: *addr, Handler: mux}
	log.Info("listening", "addr", *addr)
	if err := srv.ListenAndServe(); err != nil {
		log.Error("listen failed", "error", err)
		os.Exit(1)
	}
}

func buildManager(conf *botconf.Config, log *logpkg.Logger) *platform.DefaultManager {
	m := platform.NewManager()
	names := conf.PluginNames()
	if len(names) == 0 {
		names = pluginregistry.Names()
	}
	for _, name := range names {
		if pc, ok := conf.GetPluginConfig(name); ok {
			if _, has := pc["enabled"]; has && !conf.GetPluginBool(name, "enabled") {
				continue
			}
		}
		factory, ok := pluginregistry.Get(name)
		if !ok {
			continue
		}
		contrib, err := factory(conf, log)
		if err != nil {
			log.Error("plugin init failed", "plugin", name, "error", err)
			continue
		}
		regs := contrib.Platforms
		if len(regs) == 0 && contrib.Platform != nil {
			regs = []platform.Platform{contrib.Platform}
		}
		for _, p := range regs {
			if p != nil {
				m.Register(p)
			}
		}
	}
	return m
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func handlePing(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, 200, map[string]any{"ok": true, "time": time.Now().Unix()})
}

func handlePlatforms(w http.ResponseWriter, r *http.Request) {
	if mgr == nil {
		writeJSON(w, 500, map[string]any{"ok": false, "error": "not ready"})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "platforms": mgr.List()})
}

func handleSearch(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query().Get("q")
	pl := r.URL.Query().Get("platform")
	if pl == "" {
		pl = "applemusic"
	}
	limit := 20
	if s := r.URL.Query().Get("limit"); s != "" {
		if n, err := fmt.Sscanf(s, "%d", &limit); err == nil && n == 1 {
			// ok
		}
	}
	ctx, cancel := context.WithTimeout(r.Context(), 20*time.Second)
	defer cancel()
	tracks, err := mgr.Search(ctx, pl, q, limit)
	if err != nil {
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "tracks": tracks})
}

func handleLyric(w http.ResponseWriter, r *http.Request) {
	pl := r.URL.Query().Get("platform")
	id := r.URL.Query().Get("id")
	if pl == "" || id == "" {
		writeJSON(w, 400, map[string]any{"ok": false, "error": "platform and id required"})
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 20*time.Second)
	defer cancel()
	lyrics, err := mgr.GetLyrics(ctx, pl, id)
	if err != nil {
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "lyrics": lyrics})
}

func handleDownload(w http.ResponseWriter, r *http.Request) {
	// 最低限度：返回该曲目的直链与请求头，由客户端自行下载。
	pl := r.URL.Query().Get("platform")
	id := r.URL.Query().Get("id")
	quality := r.URL.Query().Get("quality")
	if pl == "" || id == "" {
		writeJSON(w, 400, map[string]any{"ok": false, "error": "platform and id required"})
		return
	}
	var q platform.Quality
	switch quality {
	case "high", "hifi", "320":
		q = platform.QualityHigh
	case "lossless":
		q = platform.QualityLossless
	case "hi_res":
		q = platform.QualityHiRes
	default:
		q = platform.QualityStandard // 128k MP3
	}
	ctx, cancel := context.WithTimeout(r.Context(), 20*time.Second)
	defer cancel()
	info, err := mgr.GetDownloadInfo(ctx, pl, id, q)
	if err != nil {
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{
		"ok":     true,
		"url":    info.URL,
		"urls":   info.CandidateURLs,
		"headers": info.Headers,
		"format": info.Format,
		"bitrate": info.Bitrate,
		"size": info.Size,
		"quality": info.Quality.String(),
	})
}
