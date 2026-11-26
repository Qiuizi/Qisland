# 国内天气城市表

`china-cities.json` 是 Qisland 天气城市搜索用的精简离线表，只覆盖中国的省会、地级市（含地区 / 盟 / 自治州）、直辖市、港澳，以及海南等省直辖县级市、县和敦煌；台湾列了主要城市。海外城市不在表里，搜索时回退到 Open-Meteo 地理编码。天气预报始终使用 Open-Meteo，不依赖本表以外的天气接口。

## 规模

约 380 条、约 33 KB（UTF-8 JSON）。

## 数据来源与许可

- **名称与行政区划**：中华人民共和国民政部公布的行政区划名称与代码（公开数据）。整理时参考了 [uiwjs/province-city-china](https://github.com/uiwjs/province-city-china)（MIT License）的省市列表作为核对。
- **经纬度与英文名**： [GeoNames](https://www.geonames.org/) 地名库，许可为 [Creative Commons Attribution 4.0](https://creativecommons.org/licenses/by/4.0/)。本表是对 GeoNames 中国、台湾、香港、澳门条目的精简摘录，按官方行政区名称匹配后取城市驻地坐标（避免用行政区多边形的几何中心）。

未把 [qwd/LocationList](https://github.com/qwd/LocationList) 原表打进发行包（该仓库没有 SPDX 许可证）。

表中不含新疆生产建设兵团较新的几个县级市（铁门关、双河、可克达拉、新星、白杨、草湖）：GeoNames 与 Open-Meteo 地理编码都没有能可靠对上的条目，避免写入错误坐标。

## 使用与署名

发行包内嵌本表。若再分发该 JSON 或从本表派生新的地名数据，请保留对 GeoNames 的 CC BY 4.0 署名，以及上述民政部名称来源说明。

字段：`n` 短名、`p` 省级短名、`lat` / `lon`、`pop` 人口（用于排序，不在界面展示）、`e` 英文名、`cap` 省会、`pri` 省内优先展示的城市。
