# NFCAiME AiMEIO 测试与部署指南

本文档对应 `NFCAiME-AiMEIO` 公开仓库。本仓库只提供 Relay 中间件和 `nfcaimeio.dll` 源码，不包含私有网站前后端。

## 1. 设计边界

- Relay 只中转，不解密、不保存卡片、不连接 AiMeDB。
- 手机端加密 payload，DLL 本地解密后交给 segatools。
- `segatools.ini` 只需要 3 个值：`path`、`serverUrl`、`session-key`。
- AES key 不出现在配置文件中，只在私有 App/DLL 构建中内置。
- session-key 生成页属于私有前端，不放在本仓库。

## 2. 协议

电脑端 DLL：

```text
WebSocket /{session-key}
```

手机端 App：

```text
POST /{session-key}
Content-Type: application/json
```

Relay 健康检查：

```text
GET /health
```

加密 payload：

```json
{
  "type": "encrypted",
  "encrypted": true,
  "alg": "AES-256-GCM",
  "nonce": "12-byte hex",
  "ciphertext": "hex",
  "tag": "16-byte hex"
}
```

AES-GCM 解密后的明文 JSON 继续使用原卡片结构：

```json
{
  "type": "card",
  "privateAccessCode": "00080000123456789012",
  "officialAccessCode": "50117072486840427371",
  "idm": "012e613352046c8e",
  "encrypted": false
}
```

## 3. Relay 部署

本地测试：

```bash
cd relay
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
uvicorn relay:app --host 0.0.0.0 --port 8765
```

Windows PowerShell：

```powershell
cd relay
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
uvicorn relay:app --host 0.0.0.0 --port 8765
```

公网建议放在 HTTPS/WSS 反代后面。nginx 示例：

```nginx
server {
    listen 443 ssl http2;
    server_name your-relay.example;

    ssl_certificate /etc/letsencrypt/live/your-relay.example/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/your-relay.example/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8765;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

健康检查：

```bash
curl https://your-relay.example/health
```

## 4. DLL 构建

需要 Windows、CMake、MSVC x64 toolchain。

公开调试构建：

```powershell
cmake -S .\nfcaimeio -B .\build\nfcaimeio -A x64
cmake --build .\build\nfcaimeio --config Release
```

私有正式构建需要注入 32-byte AES key：

```powershell
cmake -S .\nfcaimeio -B .\build\nfcaimeio -A x64 `
  -DNFCAIME_AES_KEY_HEX=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff

cmake --build .\build\nfcaimeio --config Release
```

输出：

```text
build\nfcaimeio\Release\nfcaimeio.dll
```

## 5. 游戏端配置

把 DLL 放到：

```text
游戏目录\NFCAiME\nfcaimeio.dll
```

`segatools.ini` 只配置 3 个值：

```ini
[aimeio]
path=NFCAiME\nfcaimeio.dll
serverUrl=https://your-relay.example/
session-key=nfcaime-xxxxxxxx
```

说明：

- `serverUrl` 支持 `http/https/ws/wss`。
- `https://your-relay.example/` 会转换为 `wss://your-relay.example/{session-key}`。
- `http://192.168.1.10:8765` 会转换为 `ws://192.168.1.10:8765/{session-key}`。
- `session-key` 必须和手机 App 里填写的一致。

## 6. 测试顺序

1. 启动 Relay。
2. 通过私有前端或手动生成 `session-key`。
3. 使用同一 AES key 私有构建 App 和 DLL。
4. 配置 `segatools.ini` 并启动游戏。
5. 访问 `/health`，确认 `online` 里出现该 `session-key`。
6. App 填同一个服务器地址和 `session-key`。
7. 进入游戏读卡界面。
8. App 发送加密 payload。
9. DLL 解密并缓存，游戏读到对应 Aime 或 FeliCa。

## 7. 常见问题

### `/health` 没有 session-key

检查：

- 游戏是否实际加载了当前 DLL。
- `path` 是否正确。
- `serverUrl` 是否能从电脑访问。
- `session-key` 是否为空或包含非法字符。
- nginx 是否支持 WebSocket Upgrade。

### App 发送后游戏没反应

检查：

- 发送后是否在 5 秒内进入读卡窗口。
- App 和 DLL 是否使用同一个 AES key。
- `nonce/ciphertext/tag` 是否是 hex 字符串。
- 当前游戏读取的是 Aime Access Code 还是 FeliCa IDm。

### 账号不对

当前第一版只转发 App 保存的卡片字段，不做真实 `aimeId` 映射。若目标服务严格校验 userId，后续需要业务服务器下发真实 `aimeId`。
