// 桌面应用入口（Windows 下发布模式隐藏控制台窗口）
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    forza_gallery_sync_lib::run()
}
