# 隐私说明 / Privacy Policy

**WorkBuddy PointPal（以下简称「本程序」）不收集、不存储、不上传任何个人信息。**

最后更新：2026-10-12 · 适用版本：1.2.18.zc 及以后

---

## 一、本程序读写什么

| 位置 | 用途 | 是否离开本机 |
|---|---|---|
| `%LOCALAPPDATA%\WorkBuddy PointPal\token.txt` | 你自己粘贴的登录凭证，**DPAPI 加密后存储** | 否 |
| `%LOCALAPPDATA%\WorkBuddy PointPal\state.ini` | 尺寸、步长、角色、左右侧等界面设置 | 否 |
| `%LOCALAPPDATA%\WorkBuddy PointPal\pet.log` | 运行日志（记录请求结果与音效状态，**不含凭证内容**） | 否 |
| `%LOCALAPPDATA%\WorkBuddy PointPal\` 下的图片与音效 | 首次运行时从 exe 内嵌副本解包 | 否 |

**本程序不会读取：**

- 浏览器数据（历史记录、Cookie 数据库、保存的密码）
- 其它应用程序的配置目录
- 你的文档、照片或任何其它个人文件
- 剪贴板内容（凭证是你自己粘贴进输入框的）

> 历史说明：1.2.17 及更早的版本曾在**首次启动时**尝试读取
> `%USERPROFILE%\.workbuddy\` 下的文件来判断有没有可复用的凭证。
> 该逻辑从未成功取到过任何东西，已在 **1.2.18** 中**完整移除**，现在不会再触碰任何外部目录。

## 二、联网行为

本程序只访问两个目的地，**没有遥测、没有统计上报、没有广告**：

| 目的地 | 触发时机 | 发送内容 |
|---|---|---|
| `https://www.workbuddy.cn/billing/meter/get-user-resource-summary` | 运行期间每 5 秒轮询一次 | 你的登录凭证与 User-Agent —— 这是查询**你自己**积分余额所必需的 |
| `https://github.com/…` | **仅当你主动点击「检查更新」** | 不携带任何账号信息或凭证 |

不点「检查更新」，就**完全没有**任何 GitHub 请求。

## 三、凭证是怎么保护的

- 凭证由你**手动**从浏览器复制粘贴，程序不会自动抓取、不会从别处扫描。
- 落盘前用 **Windows DPAPI**（`CryptProtectData`，`CurrentUser` 作用域）加密：
  密钥由 Windows 从你的登录凭据派生并保管，**不会写入任何文件**。
- 因此把 `token.txt` 拷到另一台电脑、备份进网盘、或被其它账号读到，
  拿到的都只是一串**无法解读的密文**。
- ⚠️ **它挡不住什么**：如果恶意软件已经以**你的用户身份**在你这台电脑上运行，
  它可以调用同一个解密 API 读出内容。这是 DPAPI 的固有边界，不是本程序独有的缺陷。
  在"防同机恶意软件"这个目标上，唯一有效的方案是凭证不落盘（每次启动手动粘贴），
  代价是使用体验 —— 本项目选择了 DPAPI 这个折中。

## 四、如何彻底删除

1. 卸载程序：开始菜单「WorkBuddy PointPal → 卸载」，卸载过程会询问是否一并删除数据目录；
2. 或直接删除 `%LOCALAPPDATA%\WorkBuddy PointPal\` 整个目录。

凭证同时存在于你浏览器的登录会话里 —— 若要让它彻底失效，
请到 [www.workbuddy.cn](https://www.workbuddy.cn) 退出登录。

## 五、第三方组件与素材

本程序是开源项目（MIT），**不包含任何广告、分析或第三方 SDK**。
美术与音效素材沿用上游开源项目 DSH 余额桌宠，授权范围详见仓库中的 `LICENSE`。

## 六、免责

本程序是**非官方的第三方工具**，与 WorkBuddy 官方无关，也未获官方授权。
使用者需自行从浏览器获取自己的登录凭证；因使用本软件产生的任何后果由使用者自行承担。

---

# Privacy Policy (English summary)

**WorkBuddy PointPal collects no personal data and uploads nothing.**

- It reads and writes **only its own data folder** (`%LOCALAPPDATA%\WorkBuddy PointPal\`).
- It never reads browser data, cookies, saved passwords, other applications' folders,
  your documents, or the clipboard.
- It contacts exactly two hosts: the official WorkBuddy billing endpoint (to read *your own*
  balance, using the credential you pasted yourself) and GitHub (only when you click
  "Check for updates", carrying no credentials).
- Your credential is sealed with **Windows DPAPI** (`CurrentUser` scope) before it touches disk.
  This is **not** protection against malware already running as your user account —
  such code can call the same decryption API.
- No telemetry, no analytics, no advertising, no third-party SDKs.
- This is an **unofficial third-party tool**, not affiliated with or endorsed by WorkBuddy.
