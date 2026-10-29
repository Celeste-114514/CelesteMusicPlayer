# W12 主程序图标定稿交付

用户确认浅黄色#FFF0B8底+深蓝#263B60播放符号。SVG母版Assets/AppIcon.svg；tools/Build-IconAssets.cjs固定生成13份PNG及16/20/24/32/40/48/64/128/256九档ICO。正式EXE嵌入ICO、WinUI独立窗口及托盘沿用公共Assets/AppIcon.ico；W7原生Form显式设置同一ICO。更新包清单图片、README品牌图与NSIS安装/卸载图标引用（未另构建安装器）；文件关联DefaultIcon仍指向嵌入新图标的EXE，没有修改用户默认关联。

通过w12-build.log自包含publish；验证ICO9档目录及System.Drawing.Icon32px读取成功；PNG维持既有清单尺寸（Source母版1024）。主应用未运行，测试未启动可见窗口。生成前备份outputs/icon-before-w12-20261029；交付前完整dist备份另见执行输出。根快捷方式改用明确的ICO资源以区别旧exe图标缓存，不清理全局缓存。功能图标与已验收辅助窗口不重开改造。
