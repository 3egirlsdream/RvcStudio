RVC Studio NVIDIA 安装说明
========================

1. 请从 GitHub Release 说明中的 Hugging Face 下载链接获取单文件
   RVC-Studio-NVIDIA-Setup.exe。README.txt 和 SHA256SUMS.txt 可从同一
   GitHub Release 下载，用于查看说明及校验文件完整性。

2. 双击 RVC-Studio-NVIDIA-Setup.exe，并允许管理员权限。此 EXE 已包含全部
   离线安装数据，不需要额外的 .bin 分卷。

3. 安装程序已包含程序、Python 运行环境、PyTorch CUDA 12.8 运行库、默认模型、
   索引文件及标准 VB-CABLE 官方驱动包。目标电脑无需另装 Python 或 CUDA Toolkit。

4. 如果勾选安装 VB-CABLE，Windows 可能显示驱动确认提示，安装后必须重启。
   重启登录后 RVC Studio 会自动启动。已安装标准 VB-CABLE 时不会重复安装。

5. 系统要求：64 位 Windows 10 22H2（19045）或更高版本、NVIDIA RTX 显卡，
   NVIDIA 驱动建议为 572.61 或更高版本。

6. 可使用 SHA256SUMS.txt 验证安装程序在复制或下载过程中是否损坏。

提示：主安装程序尚未使用项目方代码签名证书签名，Windows SmartScreen 可能显示提醒。
内置的 VB-CABLE 驱动安装程序保留了 VB-Audio 官方有效数字签名。
