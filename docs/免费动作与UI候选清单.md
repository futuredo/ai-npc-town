# 免费动作与 UI 候选清单

**11/08 当前优先级更新**：已购 Medieval Kingdom UI 三张皮肤图已接运行 HUD，Horde 待机/行走已接实际导航；先复用当前资源，不必再取另一套免费 UI。动作库已有可索引 BVH/BIP/Maya 源，但未完成转换/接触验收。当前利用看[资源清单](资源利用清单.md)；以下仍为免费候选研究，不能标成已取得。

核查日期：2026-11-07。用途：补当前四位 Humanoid 居民的日常动作，以及可替换视觉的基础界面。**本轮为官方商品页/作者页核查，尚未下载、导入或重定向验证。** 不把免费样例等同于完整付费包；不把动作文件数等同于独立行为数。

同日后续的[增量素材筛选](增量素材筛选与玩法建议.md)已从用户的 Fantasy Horde 包导入十段日常动作，并对六个 Humanoid 完成 60/60 组姿态变化采样：已有原地行走、待机、交谈和部分坐姿反应。**这不表示本页列出的免费候选已经取得**，也未解决拿放/递交、持物移动、脚接地和动作过渡。后续按实际缺口取片段，避免为了补少量交互重复下载整套基础库。

## 建议先获取的组合

动作先取 **Human Basic Motions FREE + Human Crafting Animations FREE**；缺少的拿放、交接、抱箱和细致情绪手势再到 Mixamo 按需求选。UI 先取 **Simple Button Set 02**，需要现成窗口/提示框时再选 **FANTASTIC UI STARTER PACK**。先统一一套组件，不混搭多套视觉；后续确定美术后替换图片、字体和配色，保留布局与交互。

## 动作：已核对免费版内容

| 优先级 | 资源与官方链接 | 免费部分实际说明 | 当前用途与限制 |
| --- | --- | --- | --- |
| 首选 | [Human Basic Motions FREE · Kevin Iglesias](https://assetstore.unity.com/packages/3d/animations/human-basic-motions-free-154271) | 待机；走8方向；跑8方向；冲刺5方向；左右转身；跳跃/下落；Talk。标称118个动画文件，有男女版本、Avatar Mask与Blender文件 | 当前最直接补走跑/待机/交谈。118不是118种独立日常行为。页面对不同动作的原地/Root Motion说明并不完全相同，取得后逐片段检查，位移交给现有导航 |
| 首选补充 | [Human Crafting Animations FREE · Kevin Iglesias](https://assetstore.unity.com/packages/3d/animations/human-crafting-animations-free-157920) | 待机、犁地、钓鱼分段、采集、地面锤击、单手采矿；标称118个文件，有男女版本、Avatar Mask | 补采集与工作表现；不是完整拿放/递交包。不能把采集动作直接算作有精确交接手位 |
| 按需补齐 | [Mixamo](https://www.mixamo.com/) / [Adobe官方FAQ](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html) | Adobe说明有Adobe ID即可免费，无需CC订阅，角色/动画可用于商业与个人游戏 | 检索 `Talking`、`Pointing`、`Nodding`、`Shaking Head`、`Picking Up`、`Standing Up`、`Carrying`、`Holding Box`、`Giving`。这些是搜索词，不是本轮确认已取得的片段列表。选FBX、原地版本(如有)，优先Without Skin。服务存在账户/地区限制，不能保证所有账户可用 |
| 备用 | [Universal Animation Library · Quaternius](https://quaternius.itch.io/universal-animation-library) | 免费Standard与收费Pro/Source分开；作者说明支持Unity重定向、原地/Root Motion，CC0 | 可补基础移动/情绪/坐姿，具体Standard清单需包内核实。120+是整库宣传，不能当免费版数量 |
| 按需要再取 | [Universal Animation Library 2 · Quaternius](https://quaternius.itch.io/universal-animation-library-2) | 免费Standard，Source收费；作者说明覆盖农活/钓鱼等，CC0，原地/Root Motion | 若当前任务确需工作类片段再补；130+也不当作Standard实测数量 |

两个Kevin免费包的商店页面均列Unity 6000.0.59f2对三种渲染管线兼容；动作自身的重定向仍需在本工程6000.3.23f1检查。商店条目采用Standard Unity Asset Store EULA，免费不等于CC0。正式取得时保留官方渠道、版本和包内许可。

### 免费方案仍未确认补齐的关键动作

| 行为 | 下一步检查 | 可接受的原型方式 |
| --- | --- | --- |
| 抬箱/抱箱移动/放箱 | 手臂高度、箱子大小、原地走路、起止片段 | 上半身持物层 + 下半身行走；物品在明确事件点绑定/解绑 |
| 双人递交/接收 | 双方距离、左右手、交接时刻 | 分别播放给/接动作，由引擎在同意/距离/持物条件满足后提交转移，不由动画台词决定 |
| 观察/思考/拒绝 | 与居民骨架和性格匹配；避免太夸张 | 少量手势 + 朝向/停顿，别给每个NPC堆大量随机表演 |
| 门/箱操作 | 手位与门轴、箱盖位置 | 角色动作与机关动画分别驱动；不能用商人箱子的1段动作代替所有交互 |

### 暂不列为首选

- [Everyday Motion Pack Free](https://assetstore.unity.com/packages/3d/animations/everyday-motion-pack-free-115067)：仍显示免费，但本次页面未读到完整动作清单；版本1.1、2018年更新、原始Unity2017。作为备选，不用旧介绍保证Unity6运行结果。
- [Motifect Daily Life Motion Pack](https://www.fab.com/listings/bea29cd8-b9ca-47d3-87d5-acc571c39735)：内容标称40段日常FBX，但商品说明只明确写了“Unreal projects”可免费商用，作者网站未能读取。本轮不把跨Unity许可当作已经确认。
- 现有居民包里的 `Kevin Iglesias(not for sale)` 测试目录继续排除。正式从作者免费包取得的内容单独登记，不能用名字相同推定原下载包授权。

## UI：来自 Unity 商店的免费候选

| 优先级 | 资源与链接 | 页面内容 | 怎么用在 Demo |
| --- | --- | --- | --- |
| 首选基础图形 | [Simple Button Set 02 · That Witch Design](https://assetstore.unity.com/packages/2d/gui/icons/simple-button-set-02-184903) | 矩形按钮、Toggle、Radio、Slider、Input Field图形；21个白色图标，多尺寸可改色；免费约820KB | 方法输入、确认/取消、机制开关、等待状态的基础外观。页面列2019.4原始版本；实际Prefab/交互实现需包内确认，不当作完整UI系统 |
| 首选成套样例 | [FANTASTIC UI STARTER PACK · MyIndie.Games](https://assetstore.unity.com/packages/2d/gui/icons/fantastic-ui-starter-pack-123733) | 当前2.0免费，作者列Unity6支持；uGUI+TMP、九宫格图形、窗口/按钮/提示框/对话等预制件与样例；约6.1MB | 可快速搭对话、物品、条件摘要面板。先按需取组件；其中背包/属性系统仅是示例，不自动替代项目的物品/约定数据 |
| 提示框补充 | [Simple Tooltip · Norb](https://assetstore.unity.com/packages/tools/gui/simple-tooltip-155804) | 免费，TMP依赖，六种样例样式，可改色；2019年版本 | 物品用途、条件说明、失败原因。若上一个UI包已经满足则不再引入 |
| 开发面板备选 | [Simple TableUI · Adrian Bouza](https://marketplace.unity.com/packages/tools/gui/simple-tableui-170718) | 免费，TMP，文本表格、边框和颜色可改 | 仅用于实验记录/成本/回执表；玩家主界面不用表格堆调试数据 |
| 控件备选 | [Interface Kit: GUI Elements · DOGAN KIRNAZ](https://assetstore.unity.com/packages/tools/utilities/interface-kit-gui-elements-318753) | 免费；20种UI元素与脚本，原始Unity6000.1.2 | 若Tab等控件有缺口再评估；已有等价组件就不重复引入框架 |

以上UI价格在本次官方页为FREE，协议为Standard Unity Asset Store EULA；尚未核实下载包。Unity6/管线兼容是作者声明，不是本机验收结果。中文字体与输入法仍需另外配置，不能用商店演示英文界面证明中文可用。

## 当前只需要的界面

1. 角色交谈窗口：历史、输入、发送/取消、NPC等待状态。
2. 方法/条件确认卡：参与者、物品/资源、地点、期限、确认/返回修改。
3. 物品与交互提示：选中对象、可做操作、失败原因。
4. 简单目标/结果：当前目标、执行进度、可引用的实际结果。
5. 独立实验面板：机制开关、回放/重置、请求与费用记录。

视觉先用一套通用图形；文字、状态与交互由工程实现。后续绘制正式UI时保留可编辑布局、九宫格边界、字体回退及不同分辨率规则。

## 下载后的验收顺序

先一位现有居民：Idle→Walk→Talk→Gather，检查脚滑/手位/衣服穿插/Root Motion；再补搬运和交接。UI先一张对话窗口和确认卡，检查中文、键盘焦点、输入法、等待可取消与720p可读性。本轮仅完成候选筛选，不增加模型调用，不修改工程依赖。
