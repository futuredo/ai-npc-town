# 原始素材

2026-11-08 当前利用情况见 `docs/资源利用清单.md`、`docs/月光驿站局内接入.md` 和日志 020。十九组已购源均保留；`art/local-asset-catalog-2026-11-08.csv` 有 20,396 条可检索包目录（含文件夹/版本，不是独立物件数）。`art/local-asset-gameplay-2026-11-08.json` 保留四批选择/复用、源包与导入后 SHA-256、官方 URP 适配和局内场景审计。已直接使用完整 KINGDOM 场景及 AFS 整组附魔工作站，不再只列样板。

保留可编辑源文件的索引。大体积 Blender / PSD / WAV 原文件先放外部共享存储，在素材清单登记 source 路径；当前尚未配置 Git LFS，不要直接批量提交大型二进制文件。

只在 Unity Assets 内存放实际需要导入的文件；记录来源、许可证、修改和导入验证结果。

2026-11-07：当前商品筛选见 `docs/素材筛选_白龙月灯_v4.md`，可筛选表见 `art/asset-candidates-v4.csv`。`asset-manifest.json` schema 2 的 `assets` 保存需求，`candidates` 保存商品页信息；`candidate_ids` 关联两者。旧 v3 需求为 `suspended_legacy`，新需求为 `candidate_research`，均不是已购或已导入。

当前工程已迁移到 URP `17.3.0`（Unity `6000.3.23f1`），接受 UE 源包导出转换。外盘样板已选择性导入本机 `unity/Assets/LocalThirdParty/`，来源/哈希见 `art/local-asset-import-2026-11-07.json`；具体迁移与验证见 `docs/开发日志/2026-11-07_016_URP管线迁移.md` 和台账 `render_migration`。该目录与 21 张粗糙度转换派生贴图均被 Git 忽略，不随仓库分发；本地备份在 `.local/urp-migration-20261107/before/`。

同日增量索引为 `art/local-asset-increment-2026-11-07.json` 和 `art/local-asset-increment-models-2026-11-07.csv`：两包 68 源资源、23 FBX、56 片段读取结果，以及十片段对六角色的姿态采样。索引分别保留源文件与导入转换后的 SHA-256，不覆盖首轮记录；新样板位于 `Review/Incremental/`，详见 `docs/增量素材筛选与玩法建议.md` 和日志 017。动作接触/过渡、人工视觉与许可仍未验收；不导入仍下载中的文件、厂商 C# 或转换插件。

取得资源时补充具体版本、文件哈希、实际文件路径、许可副本及修改记录；未取得前项目需求的 `source`、`license` 维持空值。已导入商业样板的许可也仍未核实，不因 URP 技术检查通过而批准发布。商品的 `license_claimed` 只是页面声明，不等同于 `license_verified_for_acquired_files`。付费资源与源文件不要随公开仓库分发。
