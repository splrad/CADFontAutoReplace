# WPS

## 只删除盘符图标但不删除侧边栏图标

------

### 解决方案：修改注册表

1. **打开注册表编辑器**  
   - 按 `Win + R`，输入 `regedit`，回车。
2. **导航至WPS网盘注册表项**  
   - 路径示例（根据实际路径调整）：

```
HKEY_CLASSES_ROOT\CLSID\{WPS网盘的CLSID}\ShellFolder
```

1. **修改 `Attributes` 值（关键步骤）**  
   - 找到或新建 `Attributes`（`REG_DWORD`）
   - 右键 `ShellFolder` → `新建` → `DWORD (32位) 值`，命名为 `Attributes`。
   - 双击 `Attributes`，将其值改为 **`0xf090004d`**（隐藏盘符但保留导航窗格入口）。（如果已存在 `Attributes`，直接修改其值即可。）
2. **重启资源管理器**  
   - 按 `Ctrl + Shift + Esc` 打开任务管理器 → 找到 `Windows 资源管理器` → 右键 `重新启动`。
