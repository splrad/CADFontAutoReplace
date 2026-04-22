2. - # 关闭VBS
   
     1. 关闭内核隔离
     2. 禁用 Hypervisor 启动项（核心步骤）
         即使关闭了界面开关，Windows 启动加载项可能依然保留了 Hyper-V 内核。
   
     - 右键点击开始按钮，选择 **终端管理员** 或 **命令提示符（管理员）**。
     - 输入以下命令并按回车： `bcdedit /set hypervisorlaunchtype off`
     - 看到“操作成功完成”的提示
   
     1. 关闭相关的 Windows 功能
   
     - 按下 `Win + R`，输入 `optionalfeatures` 运行。
     - 在列表中**取消勾选**以下三项（如果已勾选）：
        **Hyper-V**
        **虚拟机平台**
        **Windows 虚拟机监控程序平台**
        如提示重启则暂不重启
   
     1. 清理注册表残留（针对 ESS 增强型登录）
         由于你想保留人脸识别，Windows 可能会因为“增强型登录安全性”自动重启 VBS。
   
     - 按 `Win + R` 输入 `regedit`。
     - 定位到：`HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa`
        在右侧检查是否有 `RunAsPPL` 和 `RunAsPPLBoot`，如果有，将它们的值都改为 `0`。
     - 定位到：`HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios`
        将 WindowsHello、WindowsHelloSecureBiometrics、SecureBiometrics的`Enabled` 值改为 `0`
