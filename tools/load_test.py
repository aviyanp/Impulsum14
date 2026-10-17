#!/usr/bin/env python3
"""Paced live HTTP benchmark. Requires aiohttp. --launch uses disposable accounts/data.
This measures FUT backend requests, not gameplay simulation or game FPS.
"""
import argparse
import asyncio
import hashlib
import json
import math
import os
from pathlib import Path
import resource
import secrets
import socket
import subprocess
import tempfile
import time

import aiohttp


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def percentile(xs, p):
    values = sorted(xs)
    return round(values[min(len(values) - 1, math.ceil(p * len(values)) - 1)], 3) if values else None


async def benchmark(args, base, tokens):
    latencies, lags = [], []
    errors = 0
    late = 0
    statuses = {}
    async with aiohttp.ClientSession(connector=aiohttp.TCPConnector(limit=max(128, args.clients * 2)), timeout=aiohttp.ClientTimeout(total=10)) as session:
        for _ in range(200):
            try:
                async with session.get(base + "/healthz") as r:
                    if r.status == 200:
                        break
            except (aiohttp.ClientError, asyncio.TimeoutError):
                pass
            await asyncio.sleep(0.05)
        else:
            raise RuntimeError("Server never became ready")
        headers = []
        for token in tokens:
            async with session.post(base + "/fut/rs4/auth", headers={"Authorization": "Bearer " + token}, json={}) as r:
                if r.status != 200:
                    raise RuntimeError("Fixture authentication failed")
                headers.append({"X-UT-SID": r.headers["X-UT-SID"]})
                await r.read()
        # Establish real starter clubs; tests use representative persisted inventories.
        for i, h in enumerate(headers):
            async with session.post(base + "/fut/rs4/ut/game/fifa14/user", headers=h, json={"clubName": "Bench " + str(i), "clubAbbr": "BEN"}) as r:
                await r.read()
                if r.status != 200:
                    raise RuntimeError("Fixture club creation failed")
        routes = ["user/credits", "club?count=21", "user/credits", "transfermarket?num=21&type=player", "squad/list", "user/credits", "club?count=21", "user/credits", "squad/active", "user/credits"]
        async def request(i, n, measured):
            nonlocal errors
            path = "/fut/rs4/ut/game/fifa14/" + routes[n % len(routes)]
            begin = time.perf_counter()
            try:
                if args.mixed and n % 20 == 19:
                    response = session.put(base + "/fut/rs4/ut/game/fifa14/clientdata/benchmark", headers=headers[i], json={"sequence": n})
                else:
                    response = session.get(base + path, headers=headers[i])
                async with response as r:
                    await r.read()
                    if measured:
                        statuses[str(r.status)] = statuses.get(str(r.status), 0) + 1
                        if r.status >= 400:
                            errors += 1
            except (aiohttp.ClientError, asyncio.TimeoutError):
                if measured:
                    errors += 1
            ms = (time.perf_counter() - begin) * 1000
            if measured:
                latencies.append(ms)
            return ms
        await asyncio.gather(*(request(i, n, False) for i in range(args.clients) for n in range(4)))
        epoch = time.perf_counter() + 0.1
        intended = int(args.duration * args.hz)
        async def player(i):
            nonlocal late
            for n in range(intended):
                due = epoch + n / args.hz + (i / args.clients) / args.hz
                await asyncio.sleep(max(0, due - time.perf_counter()))
                lag = max(0, time.perf_counter() - due) * 1000
                lags.append(lag)
                ms = await request(i, n, True)
                if ms + lag > 1000 / args.hz:
                    late += 1
        await asyncio.gather(*(player(i) for i in range(args.clients)))
        elapsed = time.perf_counter() - epoch
        async with session.get(base + "/metrics") as r:
            metrics = await r.text()
        heap = next((int(line.split()[1]) for line in metrics.splitlines() if line.startswith("impulsum_gc_heap_bytes ")), None)
        return {"clients": args.clients, "requested_hz_per_client": args.hz, "server_scheduler_hz": args.tick, "duration_target_seconds": args.duration, "elapsed_seconds": round(elapsed, 3), "requests": len(latencies), "responses_per_second": round(len(latencies) / elapsed, 1), "errors": errors, "statuses": statuses, "latency_ms": {"p50": percentile(latencies, .5), "p95": percentile(latencies, .95), "p99": percentile(latencies, .99), "max": round(max(latencies), 3)}, "schedule_lag_p99_ms": percentile(lags, .99), "deadline_misses_including_schedule_lag": late, "server_gc_heap_bytes": heap, "mix": "representative starter-club reads, 5% durable writes" if args.mixed else "representative starter-club reads", "scope": "HTTP FUT backend only; same-host generator; warm caches; no FIFA executable or gameplay"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch", type=Path, help="Server DLL; starts a disposable local hosted instance")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--base-url")
    parser.add_argument("--token-file", type=Path, help="JSON array of private-server tokens for an existing instance")
    parser.add_argument("--clients", type=int, default=32)
    parser.add_argument("--hz", type=int, choices=(30, 60), default=60)
    parser.add_argument("--tick", type=int, choices=(30, 60), default=60)
    parser.add_argument("--duration", type=int, default=10)
    parser.add_argument("--mixed", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.clients <= 2000 or not 1 <= args.duration <= 120:
        parser.error("clients must be 1..2000, duration 1..120")
    with tempfile.TemporaryDirectory(prefix="impulsum-bench-") as directory:
        root = Path(directory)
        process = None
        log = (root / "server.log").open("w")
        usage_before = resource.getrusage(resource.RUSAGE_CHILDREN)
        try:
            if args.launch:
                tokens = [secrets.token_urlsafe(32) for _ in range(args.clients)]
                (root / "accounts.json").write_text(json.dumps([{"Id": 1001 + i, "Name": "Bench" + str(i), "TokenSha256": hashlib.sha256(token.encode()).hexdigest()} for i, token in enumerate(tokens)]))
                ports = [port(), port(), port()]
                base = "http://127.0.0.1:" + str(ports[0])
                (root / "server.json").write_text(json.dumps({"LocalMode": False, "BindAddress": "127.0.0.1", "PublicWebUrl": base, "WebPort": ports[0], "BlazePort": ports[1], "RedirectorPort": ports[2], "AccountsFile": str(root / "accounts.json"), "DataDirectory": str(root / "data"), "MaxPlayers": max(1000, args.clients), "MaxConnections": max(2048, args.clients * 2), "RequestsPerSecond": 10000, "TickRate": args.tick, "LogLevel": "Error"}))
                env = {k: v for k, v in os.environ.items() if not k.startswith("FIFA14_") and k != "FUT_SEED_CLUB"}
                env["FIFA14_CONFIG"] = str(root / "server.json")
                process = subprocess.Popen([args.dotnet, str(args.launch.resolve())], env=env, stdout=log, stderr=subprocess.STDOUT)
            else:
                if not args.base_url or not args.token_file:
                    parser.error("Use --launch or both --base-url and --token-file")
                base = args.base_url.rstrip("/")
                tokens = json.loads(args.token_file.read_text())
                if len(tokens) < args.clients:
                    parser.error("Not enough account credentials")
                tokens = tokens[:args.clients]
            result = asyncio.run(benchmark(args, base, tokens))
        except Exception:
            log.flush()
            print((root / "server.log").read_text()[-4000:])
            raise
        finally:
            if process:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            log.close()
            server_log_tail = (root / "server.log").read_text()[-2500:]
        usage_after = resource.getrusage(resource.RUSAGE_CHILDREN)
        if process:
            result["server_cpu_seconds_including_startup"] = round(usage_after.ru_utime + usage_after.ru_stime - usage_before.ru_utime - usage_before.ru_stime, 3)
            result["child_max_rss_mb"] = round(usage_after.ru_maxrss / 1024, 1)
            result["server_exit_code"] = process.returncode
            if process.returncode != 0:
                result["server_log_tail"] = server_log_tail
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
