// 桌面应用入口。
// 使用 Windows 控制台子系统（默认，不设置 `windows_subsystem`），保证 CLI 模式
// 在终端中同步、按顺序输出；GUI 模式双击启动时由 lib.rs 隐藏独占控制台窗口。
// CLI 模式经 AttachConsole 挂接父控制台输出。

fn main() {
    forza_gallery_sync_lib::run()
}
