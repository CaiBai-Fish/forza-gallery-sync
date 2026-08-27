// 桌面应用入口。
// 使用 Windows GUI 子系统：双击启动 GUI 时不创建任何控制台窗口（零闪现）。
// 带参数启动的 CLI 模式经 AttachConsole 挂接父控制台输出（脚本 / 定时任务正常）。
#![windows_subsystem = "windows"]

fn main() {
    forza_gallery_sync_lib::run()
}
