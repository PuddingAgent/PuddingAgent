# Pudding YAML 配置（跨平台）

最后更新：2026-02-20

## 1. 全局文件
在项目根目录创建：

- `pudding.yaml`（或 `pudding.yml`）

示例：

```yaml
providers_dir: providers
active_provider: openai/gpt-4o-mini
swarm_final_test_command: dotnet test PuddingCode.slnx -c Debug --no-build
```

字段说明：

- `providers_dir`: 服务商目录。支持相对路径、绝对路径、`~`、环境变量。
- `active_provider`: 默认激活的 provider/model，推荐格式 `providerId/modelId`。
- `swarm_final_test_command`: Swarm 合并后执行的最终测试命令。未配置时默认自动探测 `.slnx/.sln` 并执行 `dotnet test -c Debug --no-build`。

## 2. 服务商文件
在 `providers_dir` 目录下，一个服务商一个 yaml 文件。

示例：`providers/openai.yaml`

```yaml
id: openai
name: OpenAI
endpoint: https://api.openai.com/v1
api_key_env: OPENAI_API_KEY

models:
  - id: gpt-4o
    max_tokens: 8192
  - id: gpt-4o-mini
    max_tokens: 8192

billing:
  mode: per_token
  input_usd_per_million_tokens: 5
  output_usd_per_million_tokens: 15
```

兼容的计费模式：

- `per_token`
- `per_request`
- `per_session`
- `monthly_flat`
- `local_free`

## 3. 路径兼容说明（Linux/Windows）

- 支持 `/` 与 `\` 两种分隔符。
- 支持 `~`（用户主目录）。
- 支持环境变量（例如 `%USERPROFILE%` 或 `$HOME` 形式展开后路径）。
- 相对路径按 `pudding.yaml` 所在项目根目录解析。

示例：

```yaml
# Linux/macOS
providers_dir: ~/.config/pudding/providers
```

```yaml
# Windows
providers_dir: %USERPROFILE%\\pudding\\providers
```

## 4. 启动行为

CLI 启动时会：

1. 读取 `config.json`（若存在）
2. 扫描 `pudding.yaml` + `providers/*.yaml`
3. 合并服务商与模型（同 ID 会覆盖）
4. 输出扫描摘要（provider/model/file 数量）

最终 provider ID 采用：`providerId/modelId`
