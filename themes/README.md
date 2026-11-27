# Qisland 主题包（theme.json）

主题包是一个很小的 JSON 文件，**只写你想改的字段**，没写的沿用当前外观。在「设置 → 布局与 DIY → 主题包」里导入、导出、切换和恢复默认。

```json
{
  "format": "qisland-theme/1",
  "name": "极夜曜石",
  "author": "你的名字",
  "version": "1.0",
  "description": "一句话介绍",
  "material": "solid",
  "background": "#14141F",
  "backgroundEnd": "#06060B",
  "card": "#B9B4FF14",
  "accent": "#8C7DFF",
  "text": "#F2F1FA",
  "subtext": "#A6A3C2",
  "radius": 30,
  "opacity": 1,
  "blur": 0.6,
  "font": "Segoe UI Variable Text"
}
```

| 字段 | 说明 | 范围 |
| --- | --- | --- |
| `name` | 主题名（必填） | 最多 24 字 |
| `author` / `version` / `description` | 作者、版本、简介 | 可选 |
| `material` | 材质：`solid` 纯色、`frosted` 磨砂玻璃、`liquid` 液态玻璃 | 纯色按底色深浅自动走深色或白色外观 |
| `background` / `backgroundEnd` | 胶囊底色（上 / 下渐变） | 纯色透明度至少 90%，玻璃至少 15% |
| `card` | 展开后卡片底色 | 透明度 3%–78% |
| `accent` | 强调色（开关、进度、选中） | 不透明 |
| `text` / `subtext` | 主 / 副文字色 | 和底色对比度不够时自动退回默认 |
| `radius` | 展开后岛的圆角，卡片圆角按比例跟着变 | 14–34 |
| `opacity` | 整座岛的不透明度 | 0.6–1 |
| `blur` | 磨砂 / 液态玻璃的模糊强度（截屏模糊模式下生效） | 0–1 |
| `font` | 字体名，没装这个字体时自动回退 | 只允许字母、数字、空格和 `, . _ -` |

颜色写法和 CSS 一样：`#RGB`、`#RRGGBB`，或 `#RRGGBBAA`（最后两位是透明度）。

导入时每一项都会校验和钳位：认不出的字段、颜色和超范围的数字会被忽略或夹到安全范围，并在设置页列出来；
文件超过 32 KB、不是 JSON 对象或没有 `name` 时整份拒绝。所以一个写坏的主题包最多是「不好看」，不会让岛看不见或崩掉。

这里的三套是官方示例：`obsidian-night.json`（极夜曜石）、`mist-lime.json`（晨雾青柠）、`amber-gold.json`（琥珀暗金）。
