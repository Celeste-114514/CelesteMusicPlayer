#!/usr/bin/env bash
# 无头验证：每个场景跑两遍——截图（目视）+ dump-dom（抠 __state 断言）
# ⚠ 两个坑：① --user-data-dir 必须隔离（用户 Edge 实例在跑会抢占，静默不出图）；
#          ② --screenshot 必须给绝对路径（相对路径会被写丢，且不报错）。
# ⚠ 页面维度：WB_PAGE=winmain 时读 out_<场景>_winmain.html，写 shot/dom_<场景>_winmain.*，
#   main 的产物不受影响（winmain 与 main 平行页共存验证，断言代码零复制）。
set -u
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
DIR="C:/Users/MSI/source/repos/CelesteMusicPlayer/_webcheck"
PROFILE="C:/Users/MSI/AppData/Local/Temp/edge_headless_celeste"
cd "$DIR" || exit 1
PAGE="${WB_PAGE:-main}"
if [ "$PAGE" = "main" ]; then SFX=""; else SFX="_$PAGE"; fi
# dsp 场景跑的是 dsp.html（另一张页，批次 1 没动它）——只在 main 套里跑，
# 产物不带页后缀（out_dsp.html / dom_dsp.html，老断言零改动）
ALL="songs favs recent ratings queue most albdetail artdetail artwall xtalbum placeholder folders foldersload folderssearch foldersempty tagsort tagsortsort tagsortgroup tagsortempty plwall plwalldetail plwallempty np skin"
[ "$PAGE" = "main" ] && ALL="$ALL dsp"
SCENS="${@:-$ALL}"
for s in $SCENS; do
  sfx="$SFX"; [ "$s" = "dsp" ] && sfx=""
  f="$DIR/out_${s}${sfx}.html"
  [ -f "$f" ] || { echo "MISSING $f"; continue; }
  url="file:///$f"
  "$EDGE" --headless=new --disable-gpu --hide-scrollbars --window-size=1660,980 \
    --user-data-dir="$PROFILE" --virtual-time-budget=4000 \
    --screenshot="$DIR/shot_${s}${sfx}.png" "$url" >/dev/null 2>&1
  "$EDGE" --headless=new --disable-gpu --user-data-dir="$PROFILE" \
    --virtual-time-budget=4000 --dump-dom "$url" > "$DIR/dom_${s}${sfx}.html" 2>/dev/null
  sz=$(wc -c < "$DIR/shot_${s}${sfx}.png" 2>/dev/null || echo 0)
  echo "== $s: png ${sz}B dom $(wc -c < "$DIR/dom_${s}${sfx}.html")B"
done
