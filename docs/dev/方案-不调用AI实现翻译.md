# 不调用 AI 实现翻译：可行性 + 接入 nextai-translator 的交互

> 针对问题：「参考 nextai-translator 的『划句→图标→弹窗』，但不要基于 AI，能不能翻译？」
> 结论先说：**能。而且这个项目自己就已经有一半在不用 AI 了。**

---

## 0. 一句话答案

| 部分 | 依赖 AI 吗 | 能否替换 |
|---|---|---|
| 选中文字 → 出现小图标 → 点击弹窗 | ❌ 完全不依赖 | 1:1 复刻，约 200 行 |
| 整句翻译 | ✅ 现在走 LLM | 换成 **Chrome/Edge 内置翻译模型**（本地、零 key、零成本）或免费机翻接口 |
| 单个单词查询 | ❌ **已经是免费 API** | 直接沿用，还能加本地词典 |

---

## 1. 先看它到底哪部分用了 AI（main 分支实读）

| 文件 | 作用 | 是否 AI |
|---|---|---|
| `src/browser-extension/content_script/index.tsx` | 监听 mouseup、取选区、画图标、开卡片 | **否** |
| `src/common/translate.ts` | 拼 prompt、调引擎、透传流式结果 | 是（编排层） |
| `src/common/engines/*.ts` | OpenAI / Claude / Gemini / Ollama… 共 17 家 | 是 |
| `src/common/services/wordLookup.ts` | 查单词，请求 `api.dictionaryapi.dev` | **否** |
| `src/common/translate.ts` 里的 `isAWord()` | 用 `Intl.Segmenter` 判断是否单词 | **否** |

**关键发现**：`wordLookup.ts` 已经在用免费、无 key 的 [dictionaryapi.dev](https://dictionaryapi.dev) 查单词。也就是说"非 AI 通道"在这套代码里是既成事实，只是没被用在句子上。

---

## 2. 一个必须先搞清楚的坑：不能直接套 `IEngine`

`translate.ts` 在调引擎前的最后一行是：

```ts
commandPrompt = `Only reply the result and nothing else. ${commandPrompt}:\n\n${contentPrompt.trimEnd()}`
```

也就是说 **引擎收到的是"带指令 + 原文"的整段 prompt**，不是纯净的待翻译文本。

如果你写一个 `NativeMT extends AbstractEngine`，把 `req.commandPrompt` 直接丢给机器翻译，结果里会混进 `Only reply the result and nothing else...` 这些指令——**翻译结果直接废掉**。

所以有两条路：

- ✅ **推荐**：在 `translate.ts` 里加一个"MT 分支"，直接用 `query.text`（原始选区文本），完全绕过 prompt 构造。
- ⚠️ 不推荐：在引擎层做 prompt 剥离（靠正则截断），脆弱且随语言配置变化就会崩。

---

## 3. 补丁：给 `translate.ts` 加一条非 AI 通道

在 `await getSettings()` 之后、`getEngine(effectiveProvider)` 之前插入：

```ts
// src/common/translate.ts
const settings = await getSettings()
markPerf('settings')

// ★ 非 AI 通道：直接用原文，不经过 prompt
if (settings.provider === 'NativeMT') {
  const toChrome = (code: string) =>
    code.startsWith('zh-Hans') ? 'zh' : code.startsWith('zh-Hant') ? 'zh-Hant' : code.split('-')[0]
  try {
    const out = await nativeTranslate(query.text, toChrome(query.detectFrom), toChrome(query.detectTo))
    await query.onMessage({ content: out, role: 'assistant', isWordMode: false, isFullText: true })
    query.onFinish('stop')
  } catch (e) {
    query.onError(e instanceof Error ? e.message : String(e))
  }
  return
}
```

配套适配器（新文件 `src/common/engines/native-mt.ts`）：

```ts
type AnyTranslator = { translate: (t: string) => Promise<string> }
let cached: { pair: string; t: AnyTranslator } | null = null

export async function nativeTranslate(text: string, src: string, dst: string): Promise<string> {
  const g = globalThis as unknown as { Translator?: any }
  if (!g.Translator) throw new Error('当前浏览器不支持内置翻译（需 Chrome 138+ 桌面版 / Edge）')

  const pair = `${src}>${dst}`
  if (!cached || cached.pair !== pair) {
    const avail = await g.Translator.availability({ sourceLanguage: src, targetLanguage: dst })
    if (avail === 'unavailable') throw new Error(`不支持的语言对：${src} → ${dst}`)
    // avail 可能是 'downloadable'，create() 会触发首次下载，要给 UI loading 态
    cached = { pair, t: await g.Translator.create({ sourceLanguage: src, targetLanguage: dst }) }
  }
  return cached.t.translate(text)
}
```

> `isFullText: true` 是这套接口里为"非流式引擎一次性返回"预留的标记，正好适合机翻——不用假装流式。

---

## 4. 三条非 AI 翻译通道，怎么选

| 通道 | 质量 | 成本 | 需要 key | 离线 | 国内可用 | 稳定性 | 备注 |
|---|---|---|---|---|---|---|---|
| **Chrome/Edge 内置 Translator API** | 中上 | 0 | ❌ | ✅ 模型在本机 | ✅ | 高（跟浏览器走） | **首选**，`window.Translator`，Chrome 138+ 桌面版 |
| Google 免费接口 `translate_a/single?client=gtx` | 中上 | 0 | ❌ | ❌ | ⚠️ 经常不通 | 中（非公开接口，随时变） | 只能做兜底 |
| Bing 非官方（`bing-translate-api`，MIT） | 中上 | 0 | ❌ | ❌ | ✅ | 中（需刷 token） | MouseTooltipTranslator 在用 |
| 自建 [LibreTranslate](https://github.com/LibreTranslate/LibreTranslate)（17.8k, AGPL） | 中 | 服务器 | ❌ | ✅ | ✅ | 高 | Docker 一键，适合长期 |
| 本地 [Argos Translate](https://github.com/argosopentech/argos-translate)（6.7k, MIT） | 中 | 0 | ❌ | ✅ | ✅ | 高 | Python 离线包，en↔zh 可用 |
| [Bergamot](https://github.com/browsermt/bergamot-translator) WASM（571, MPL-2.0） | 中 | 0 | ❌ | ✅ | ✅ | 低 | 2024 已停更 |
| 纯词典（仅单词） | — | 0 | ❌ | ✅ | ✅ | 高 | `dictionaryapi.dev`（现有）/ [ECDICT](https://github.com/skywind3000/ECDICT) 本地 77 万词条 |

**推荐组合**：

```
单词/短语 ──→ 本地 ECDICT（词形还原后查，0ms、离线）──未命中──→ dictionaryapi.dev
整句/段落 ──→ ① Chrome 内置模型 ──失败──→ ② Bing 非官方 ──失败──→ ③ LibreTranslate 自建
```

全部通道都不需要账号、不需要付费、不需要 key。

---

## 5. 交互层怎么搬（这部分一行 AI 都不用）

从源码提炼的关键机制，可以直接照做：

1. **触发**：`document.addEventListener('mouseup')` → **`setTimeout(..., 0)` 之后再读 `window.getSelection()`**。选区在 mouseup 的同一 tick 里还不稳定，这是必须的。
2. **补漏**：选不到文本时，回退读 `input`/`textarea` 的 `selectionStart/selectionEnd`。
3. **图标**：一个 20×20 的绝对定位 `div`，`left/top` 用**页面坐标**（`pageX/pageY + 7`）；用 `visibility` 切换，不要销毁重建。
4. **点击**：图标 `click` → `showPopupCard($popupThumb, text)`，在 Shadow DOM 容器里 `createRoot` 挂 React。
5. **定位**：把鼠标坐标包成一个 `@floating-ui/dom` 的虚拟参考元素——
   ```ts
   showPopupCard({ getBoundingClientRect: () => new DOMRect(x, y, 7, 7) }, text)
   ```
6. **收放**：`mousedown` 收起图标和卡片；`settings.pinned` 为真时卡片保留。
7. **常量**：`zIndex = 2147483647`、`popupCardOffset = 7`、卡片 `minWidth 220 / maxWidth 660`。

### 四个必须避开的坑

- **图标的 `mousedown` 要 `preventDefault()`**，否则点击瞬间选区被清空，`getSelection()` 就空了。（原项目在 `mousedown` 里隐藏图标，靠事件顺序侥幸成立，自己写别学这步。）
- **图标位置要 clamp 进视口**，否则在屏幕右下角选中时图标会跑出可见区。
- **卡片定位**：原项目用 `position: absolute` + 页面坐标，页面滚动后要重算；自己写建议直接 `position: fixed` + `clientX/clientY`，省掉这层账。
- **`all_frames: true`**，否则 iframe 里的文字选不中；同时 Shadow DOM 隔离样式，避免被宿主页面 CSS 污染。

---

## 6. 要验证的 5 件事（3 分钟）

跑我给的 demo（`demo/index.html`，见第 8 节），页面顶部有环境自检芯片：

1. `内置翻译：存在` → 你的 Chrome 是 138+ 桌面版
2. `en→zh：available` 或 `downloadable` → 能用（downloadable 表示首次会下载模型，几十 MB）
3. `词典 API：可用` → 单词通道 OK
4. `免费 MT 接口：不可达` → **正常**，那是 CORS 挡的；有内置模型就不需要它
5. 做扩展后还要单独验证：**content script 的隔离世界里能不能访问 `window.Translator`**。如果不行，改走 `runtime.sendMessage` 到 background，或用 offscreen document。

---

## 7. 许可证：这条要注意

`nextai-translator` 是 **AGPL-3.0**。

- 看它的代码、学它的交互思路 → 没问题
- **fork 改完再分发（上架商店、做成 SaaS）→ 必须开源你的全部代码**，AGPL 会传染

所以建议：**交互层自己写**（核心就 200 行左右，上面第 5 节已经把机制拆完了），只吸收思路，不复制代码。翻译通道全部是公开接口或浏览器原生 API，没有许可证问题。

---

## 8. 附：立即可跑的 Demo

```
demo/
├── index.html    单页 demo：选中 → 图标 → 弹窗，内置模型 + 免费词典降级
└── serve.mjs     极简静态服务器（内置 AI 需要安全上下文，别用 file:// 打开）
```

运行：

```powershell
cd demo
node serve.mjs
# 浏览器打开 http://localhost:8123/
```

页面顶部会显示这台机器上每条通道的真实可用状态；选中英文后点小图标，卡片里会标出「这条译文是谁给的、花了多少 ms」。
