# 托盘与开发运行

`AiLocalApi.Desktop.exe` 打开管理窗口；`--tray` 仅显示托盘。关闭窗口只隐藏，暂停不必要刷新；托盘可再次打开。退出命令使用显式关闭生命周期，依次停止接单、HTTP 服务、下载、调度与 Worker。锁屏不是退出信号，API 和任务继续运行；注销/会话结束由 Avalonia 的 ShutdownRequested 进入协调退出。

每个 Windows 用户只允许一个实例：当前用户 SID 命名的 Local mutex + CurrentUserOnly 管道；再次打开唤醒已有窗口，不启动另一个端口。`AiLocalApi.Desktop.exe --exit` 向当前用户实例发送退出请求，供本地验证使用，不额外创建后台宿主。

端口默认 17840，可在界面修改并重启，开发也可通过 `AILOCAL_PORT` 覆盖。端口占用时托盘和窗口给出错误，不假报 API 可用。`/health` 可单独验证服务可达性。

登录启动默认关闭。开启后注册当前用户 Run 项，值为当前 apphost 的完整路径加 `--tray`，不需要管理员权限。开发期间移动/删除构建目录会导致该入口失效，应在新路径重新保存开关。安装发行阶段应改用稳定安装入口；本次没有安装 NSIS、Velopack、Windows Service 或修改全局系统自启。
