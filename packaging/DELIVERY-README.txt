RVC Studio NVIDIA 安装说明
========================

1. 请下载同一 GitHub Release 中的 RVC-Studio-NVIDIA-Setup.exe 和全部
   RVC-Studio-NVIDIA-Setup-*.bin 分卷，并放在同一个目录。README.txt 和
   SHA256SUMS.txt 是说明及校验文件。

2. 双击 RVC-Studio-NVIDIA-Setup.exe，并允许管理员权限。安装时不要移动或
   删除同目录的 .bin 分卷。

3. 安装程序已包含程序、Python 运行环境、PyTorch CUDA 12.8 运行库、默认模型、
   索引文件及标准 VB-CABLE 官方驱动包。目标电脑无需另装 Python 或 CUDA Toolkit。

4. 如果勾选安装 VB-CABLE，Windows 可能显示驱动确认提示，安装后必须重启。
   重启登录后 RVC Studio 会自动启动。已安装标准 VB-CABLE 时不会重复安装。

5. 系统要求：64 位 Windows 10 22H2（19045）或更高版本、NVIDIA RTX 显卡，
   NVIDIA 驱动建议为 572.61 或更高版本。

6. 可使用 SHA256SUMS.txt 验证安装程序在复制或下载过程中是否损坏。

提示：主安装程序尚未使用项目方代码签名证书签名，Windows SmartScreen 可能显示提醒。
内置的 VB-CABLE 驱动安装程序保留了 VB-Audio 官方有效数字签名。
