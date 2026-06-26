import asyncio
import logging
import re
import time
from dataclasses import dataclass
from typing import Literal

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from pydantic import BaseModel, Field


INSTANCE_RE = re.compile(r"^[A-Za-z0-9_-]{4,80}$")
RATE_LIMIT_SECONDS = 5

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("nfcaime-aimeio-relay")

app = FastAPI(title="NFCAiME AiMEIO Relay", version="0.1.0")


class CardInPayload(BaseModel):
    type: Literal["card", "aime", "felica", "encrypted"]
    value: str | None = Field(default=None, min_length=1, max_length=64)
    mode: str | None = Field(default=None, max_length=48)
    privateAccessCode: str | None = Field(default=None, max_length=64)
    officialAccessCode: str | None = Field(default=None, max_length=64)
    idm: str | None = Field(default=None, max_length=64)
    aimeId: int | None = Field(default=None, ge=1, le=4_294_967_295)
    label: str | None = Field(default=None, max_length=80)
    alg: str | None = Field(default=None, max_length=32)
    nonce: str | None = Field(default=None, max_length=64)
    iv: str | None = Field(default=None, max_length=64)
    ciphertext: str | None = Field(default=None, max_length=4096)
    tag: str | None = Field(default=None, max_length=64)
    encrypted: bool = False


@dataclass
class InstanceConnection:
    websocket: WebSocket
    connected_at: float
    last_forward_at: float = 0


connections: dict[str, InstanceConnection] = {}
lock = asyncio.Lock()


@app.get("/health")
async def health() -> dict[str, object]:
    async with lock:
        online = sorted(connections.keys())
    return {"ok": True, "online": online}


@app.websocket("/{instance_id}")
async def pc_agent_socket(websocket: WebSocket, instance_id: str) -> None:
    instance_id = normalize_instance_id(instance_id)
    validate_instance_id(instance_id)
    await websocket.accept()

    async with lock:
        old = connections.get(instance_id)
        connections[instance_id] = InstanceConnection(websocket=websocket, connected_at=time.time())

    if old is not None:
        await safe_close(old.websocket, code=4000, reason="replaced")

    logger.info("pc connected instance=%s", instance_id)
    try:
        while True:
            await websocket.receive_text()
    except WebSocketDisconnect:
        pass
    finally:
        async with lock:
            current = connections.get(instance_id)
            if current is not None and current.websocket is websocket:
                connections.pop(instance_id, None)
        logger.info("pc disconnected instance=%s", instance_id)


@app.post("/{instance_id}", status_code=202)
async def send_card(instance_id: str, payload: CardInPayload) -> dict[str, object]:
    instance_id = normalize_instance_id(instance_id)
    validate_instance_id(instance_id)
    validate_payload(payload)

    now = time.time()
    async with lock:
        connection = connections.get(instance_id)
        if connection is None:
            raise HTTPException(status_code=404, detail="instance offline")
        remaining = RATE_LIMIT_SECONDS - (now - connection.last_forward_at)
        if remaining > 0:
            raise HTTPException(status_code=429, detail=f"rate limited, retry in {remaining:.1f}s")
        connection.last_forward_at = now

    message = payload.model_dump(exclude_none=True)
    message["receivedAt"] = int(now * 1000)
    try:
        await connection.websocket.send_json(message)
    except RuntimeError as exc:
        async with lock:
            if connections.get(instance_id) is connection:
                connections.pop(instance_id, None)
        raise HTTPException(status_code=503, detail="instance disconnected") from exc

    logger.info(
        "forwarded instance=%s type=%s card=%s encrypted=%s",
        instance_id,
        payload.type,
        masked_payload(payload),
        payload.encrypted,
    )
    return {"ok": True}


def normalize_instance_id(value: str) -> str:
    return value.strip().strip("/")


def validate_instance_id(value: str) -> None:
    if not INSTANCE_RE.fullmatch(value):
        raise HTTPException(status_code=400, detail="invalid instance id")


def validate_payload(payload: CardInPayload) -> None:
    if payload.encrypted or payload.type == "encrypted":
        nonce = normalize_hex(payload.nonce or payload.iv)
        ciphertext = normalize_hex(payload.ciphertext)
        tag = normalize_hex(payload.tag)
        if not re.fullmatch(r"[0-9a-fA-F]{24}", nonce):
            raise HTTPException(status_code=422, detail="nonce must be 12 bytes hex")
        if not ciphertext or len(ciphertext) % 2 != 0:
            raise HTTPException(status_code=422, detail="ciphertext must be hex bytes")
        if not re.fullmatch(r"[0-9a-fA-F]{32}", tag):
            raise HTTPException(status_code=422, detail="tag must be 16 bytes hex")
        return

    if payload.type == "card":
        private_access_code = normalize_digits(payload.privateAccessCode)
        official_access_code = normalize_digits(payload.officialAccessCode)
        idm = normalize_hex(payload.idm)
        has_value = any([private_access_code, official_access_code, idm])
        if not has_value:
            raise HTTPException(status_code=422, detail="card payload must include at least one card value")
        if private_access_code and not re.fullmatch(r"\d{20}", private_access_code):
            raise HTTPException(status_code=422, detail="privateAccessCode must be 20 digits")
        if official_access_code and not re.fullmatch(r"\d{20}", official_access_code):
            raise HTTPException(status_code=422, detail="officialAccessCode must be 20 digits")
        if idm and not re.fullmatch(r"[0-9a-fA-F]{16}", idm):
            raise HTTPException(status_code=422, detail="idm must be 16 hex chars")
        return

    if payload.value is None:
        raise HTTPException(status_code=422, detail="value is required")
    value = normalize_hex(payload.value)
    if payload.type == "aime":
        value = normalize_digits(payload.value)
        if not re.fullmatch(r"\d{20}", value):
            raise HTTPException(status_code=422, detail="aime value must be 20 digits")
    elif payload.type == "felica":
        if not re.fullmatch(r"[0-9a-fA-F]{16}", value):
            raise HTTPException(status_code=422, detail="felica value must be 16 hex chars")


def normalize_digits(value: str | None) -> str:
    return "" if value is None else re.sub(r"\D", "", value)


def normalize_hex(value: str | None) -> str:
    return "" if value is None else value.replace(" ", "").replace(":", "")


def mask_value(value: str | None) -> str:
    normalized = normalize_hex(value)
    if len(normalized) <= 4:
        return "****"
    return f"****{normalized[-4:]}"


def masked_payload(payload: CardInPayload) -> str:
    if payload.encrypted or payload.type == "encrypted":
        return f"encrypted=yes nonce={mask_value(payload.nonce or payload.iv)}"
    if payload.type == "card":
        return " ".join(
            [
                f"private={mask_value(payload.privateAccessCode)}",
                f"official={mask_value(payload.officialAccessCode)}",
                f"idm={mask_value(payload.idm)}",
            ]
        )
    return f"value={mask_value(payload.value)} mode={payload.mode or ''}"


async def safe_close(websocket: WebSocket, code: int, reason: str) -> None:
    try:
        await websocket.close(code=code, reason=reason)
    except RuntimeError:
        pass
