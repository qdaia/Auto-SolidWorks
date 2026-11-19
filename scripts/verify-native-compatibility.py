"""Run the basic native compatibility check through the locally built public MCP."""
import argparse
import json
import math
import os
from pathlib import Path
import queue
import subprocess
import threading
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("year", type=int)
parser.add_argument("output", type=Path, help="New directory for the part and receipts")
parser.add_argument("--plugin", type=Path, default=Path(__file__).resolve().parents[1] / "plugins/auto-solidworks")
args = parser.parse_args()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
runtime = args.plugin.resolve() / "runtime"
env = dict(os.environ, CAD_SOLIDWORKS_PIPE="compat-" + uuid.uuid4().hex,
           CAD_SOLIDWORKS_EXECUTOR=str(runtime / "executor/CadModeling.Executor.SolidWorks.exe"),
           AUTO_SOLIDWORKS_TOOL_PROFILE="full", CAD_EXECUTOR_LOG_DIR=str(output / "logs"),
           AUTO_SOLIDWORKS_ARTIFACT_DIRECTORY=str(output / "artifacts"))
# Share the normal native lease across all pipe names, including other clients.
lines = queue.Queue()
serial = 0

def send(method, params, *, notify=False):
    global serial
    message = dict(jsonrpc="2.0", method=method, params=params)
    if not notify:
        serial += 1
        message["id"] = serial
    proc.stdin.write(json.dumps(message) + "\n")
    proc.stdin.flush()
    if notify:
        return
    while True:
        line = lines.get(timeout=360)
        if not line:
            raise RuntimeError("MCP closed stdout")
        response = json.loads(line)
        if response.get("id") == serial:
            if "error" in response:
                raise RuntimeError(response["error"])
            return response["result"]

def call(name, **arguments):
    result = send("tools/call", dict(name=name, arguments=arguments))
    (output / (name + ".json")).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    if result.get("isError"):
        raise RuntimeError(result["content"])
    body = result.get("structuredContent")
    if body is None:
        body = json.loads(result["content"][0]["text"])
    return body.get("result", body)

with (output / "stderr.log").open("w", encoding="utf-8") as log:
    proc = subprocess.Popen([str(runtime / "mcp/AutoSolidWorks.ModelingMcp.exe")],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
                            env=env, text=True, encoding="utf-8")
    def read():
        for line in proc.stdout:
            lines.put(line)
        lines.put("")
    threading.Thread(target=read, daemon=True).start()
    try:
        send("initialize", dict(protocolVersion="2025-06-18", capabilities={},
                                clientInfo=dict(name="interop-compatibility", version="1")))
        send("notifications/initialized", {}, notify=True)
        health = call("cad_executor_health")
        revision = health.get("solid_works_revision") or health.get("solidworks_revision")
        if not health["available"] or int(revision.split(".")[0]) != args.year - 1992:
            raise RuntimeError(health)
        part = output / ("plate-" + uuid.uuid4().hex[:8] + ".SLDPRT")
        plan = call("cad_create_model_plan", text="80 x 50 x 10 mm plate", nativeOutputPath=str(part), responseDetail="full")
        if not plan["success"]:
            raise RuntimeError(plan)
        build = call("cad_build_model", irJson=plan["ir_json"], responseDetail="full")
        if not build["success"] or not part.is_file():
            raise RuntimeError(build)
        inspection = call("cad_inspect_model", inputPath=str(part), responseDetail="full")
        if not inspection["success"] or not inspection["model_reopened"]:
            raise RuntimeError(inspection)
        geometry = inspection["geometry"]
        if geometry["solid_body_count"] != 1 or not math.isclose(geometry["volume_mm3"], 40000, abs_tol=.01):
            raise RuntimeError(geometry)
        for axis, expected in zip("xyz", (80, 50, 10)):
            if not math.isclose(geometry["bounding_box_mm"][axis], expected, abs_tol=.01):
                raise RuntimeError(geometry)
        print(json.dumps(dict(revision=revision, part=str(part), geometry=geometry), ensure_ascii=False))
    finally:
        proc.stdin.close()
        proc.wait(timeout=30)
