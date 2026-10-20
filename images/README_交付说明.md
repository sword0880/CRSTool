# 小财牛（XiaoCaiNiu）桌面 Logo 素材包

品牌名称：小财牛  
品牌口号：财富有数，投资有度  
源设计：已确认的红橙渐变背景、侧面微笑牛头、方孔金币图标。没有重新设计造型。

## 文件清单

- `XiaoCaiNiu.ico`：Windows 多尺寸 ICO，含 16、24、32、48、64、128、256 px。
- `PNG/XiaoCaiNiu_16.png` ~ `PNG/XiaoCaiNiu_1024.png`：同一图案的 9 种独立尺寸 PNG，分别为 16/24/32/48/64/128/256/512/1024 px。
- `PNG/XiaoCaiNiu_OriginalCut_1124.png`：从生成图中提取、外部透明、未按尺寸缩小的母版；适合作为后续尺寸转换输入。
- `Original_Approved_Logo.png`：原始已确认的生成图，留档用（原始白色画布不是透明背景）。
- `Icon_Size_Preview.png`：多尺寸预览，供 UI 验收。

## 图标透明度

ICO 和尺寸 PNG 均将圆角矩形外的白色画布处理为透明；圆角内白色牛头完整保留。图标主体圆角/红橙渐变沿用已确认的图形。

## WPF / .NET 项目集成示例

1. 将 `XiaoCaiNiu.ico` 放到项目 `Assets/Brand/XiaoCaiNiu.ico`。
2. 在桌面应用 `.csproj` 文件的 `PropertyGroup` 中设置：

```xml
<ApplicationIcon>Assets\Brand\XiaoCaiNiu.ico</ApplicationIcon>
```

3. 如需窗口左上角图标，在 WPF 对应 `Window` 上设置：

```xml
Icon="/Assets/Brand/XiaoCaiNiu.ico"
```

并确保项目将图标作为 WPF Resource 收录。如果采用其他资源组织方式，按实际目录调整。
4. 如需界面中展示高清品牌图形，首选 `PNG/XiaoCaiNiu_256.png` 或 `PNG/XiaoCaiNiu_512.png`。
5. Windows 图标可能被系统缓存；更换图标后，如仍显示旧图标，尝试重新生成、替换快捷方式或刷新图标缓存。

## 品质验收

- ICO 文件可识别的内部尺寸：16、24、32、48、64、128、256 px。
- 16/24 px：以牛的整体轮廓为主，不保证小金币细节完整可读。
- 所有 PNG 的像素为 RGBA，外围透明，可用于 Windows 浅色、深色背景。
- 图标无需文字和口号；这些适合欢迎页、关于页、官网等大尺寸场景。
