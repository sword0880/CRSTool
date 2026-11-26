# 本机 Worker 部署

用户已授权在此仓库完成会影响 Worker 发行包的更新后，直接部署到本机 `CRSBackgroundService`，无需逐次询问。验证相关测试和发行清单后，用 `tools/background/Upgrade-Service.ps1` 执行事务式升级，并核对服务健康、插件状态和已安装的发行指纹。部署失败时说明当前服务状态。此授权适用于本机开发部署。
