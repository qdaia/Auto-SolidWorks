"""Read existing Auto SolidWorks receipts once; never invoke or mutate CAD."""
import argparse
import hashlib
import json
from pathlib import Path
import time


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def checks_summary(verification):
    require(isinstance(verification, dict), "Missing verification receipt")
    checks = verification.get("checks")
    require(isinstance(checks, list) and checks, "Missing declared check results")
    failed = [c for c in checks if c.get("passed") is not True]
    return {
        "passed": verification.get("passed") is True and not failed,
        "check_count": len(checks),
        "failed_count": len(failed),
        "failed_checks": [{"id": c.get("id"), "status": c.get("status"),
                           "message": c.get("message")} for c in failed[:12]],
        "failed_checks_truncated": len(failed) > 12,
        "scope": verification.get("scope"),
        "whole_model_inventory_checked": verification.get("whole_model_inventory_checked"),
    }


def summarize(receipt_path, receipt_sha256, views, reason, source_path=None, source_sha256=None):
    require(bool(source_path) == bool(source_sha256), "Supply both source path and source SHA256")
    if source_path:
        require(Path(source_path).is_absolute(), "Use an absolute source path")
        require(sha256(source_path).lower() == source_sha256.lower(), "Source changed after planning")
    require(Path(receipt_path).is_absolute(), "Use an absolute receipt path")
    require(len(receipt_sha256) == 64, "Supply the immutable tool receipt SHA256")
    require(sha256(receipt_path).lower() == receipt_sha256.lower(), "Receipt hash mismatch")
    document = json.loads(Path(receipt_path).read_text(encoding="utf-8-sig"))
    payload = document.get("payload", document)
    require(not payload.get("pending") and not payload.get("outcome_unknown"),
            "Pending or unknown build cannot be finalized")
    if isinstance(payload.get("execution"), dict):
        payload = payload["execution"]
    require(payload.get("success") is True and payload.get("status") == "completed",
            "Build is not completed successfully")
    require(not payload.get("pending") and not payload.get("outcome_unknown"),
            "Pending or unknown build cannot be finalized")
    evidence = payload.get("evidence", [])
    require(isinstance(evidence, list), "Missing lifecycle evidence")
    require(all(e.get("passed") is not False for e in evidence), "Failed lifecycle evidence")
    readbacks = [e for e in evidence if e.get("stage") == "saved_model_readback"]
    require(len(readbacks) == 1 and readbacks[0].get("passed") is True,
            "Missing successful final saved-file readback")
    native = Path(payload.get("native_path") or "")
    require(native.is_absolute() and native.is_file(), "Missing saved native model")
    model_hash = sha256(native)
    require(model_hash.lower() == str(readbacks[0].get("data", {}).get("sha256")).lower(),
            "Model changed after saved-file acceptance")
    reports = [e for e in evidence if e.get("stage") == "review_report"]
    require(len(reports) == 1 and reports[0].get("passed") is True,
            "Missing final review report")
    report_path = Path(reports[0].get("data", {}).get("path") or "")
    require(report_path.is_absolute(), "Review report path must be absolute")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    saved = report.get("saved_model_readback")
    require(isinstance(saved, dict), "Missing saved-model inspection")
    require(saved.get("success") is True and saved.get("model_reopened") is True
            and saved.get("rebuild_succeeded") is True and saved.get("capture_complete") is True,
            "Incomplete saved-model inspection")
    require(Path(saved.get("input_path") or "").resolve() == native.resolve(),
            "Saved inspection targets a different model")
    require(str(saved.get("model_sha256")).lower() == model_hash.lower(),
            "Saved inspection hash mismatch")
    body = saved.get("body_validity", {})
    require(body.get("complete") is True and body.get("passed") is True
            and body.get("fault_count") == 0, "Missing or failed native body validity")
    before = checks_summary(payload.get("verification"))
    after = checks_summary(saved.get("verification"))
    require(before["passed"] and after["passed"], "Declared source verification failed")
    require(payload.get("geometry_status") == saved.get("geometry_status") == "passed",
            "Geometry acceptance missing")
    require(reason.strip(), "State why the selected views cover this source")
    require(views and len(views) == len(set(views)) and "isometric" in views,
            "Unique views including isometric are required")
    previews = {}
    for e in evidence:
        if e.get("stage") == "preview" and e.get("passed") is True:
            data = e.get("data", {})
            previews[data.get("view")] = data.get("path")
    selected = []
    for view in views:
        require(view in previews, "Requested preview is not in the build receipt: " + view)
        p = Path(previews[view])
        require(p.is_absolute() and p.is_file(), "Missing preview: " + view)
        selected.append({"view": view, "path": str(p), "sha256": sha256(p)})
    quality = [(f.get("name"), f.get("sketch_quality")) for f in saved.get("features", [])
               if isinstance(f.get("sketch_quality"), dict)]
    needs_review = [name for name, q in quality if q.get("status") != "passed"]
    return {
        "workflow_revision": "essential-final-review-1",
        "success": True, "native_path": str(native), "model_sha256": model_hash,
        "request_id": payload.get("request_id"),
        "saved_reopened_rebuilt": True, "body_validity": body,
        "geometry": saved.get("geometry"), "geometry_status": saved.get("geometry_status"),
        "parametric_status": saved.get("parametric_status"),
        "sketch_count": len(quality), "sketches_requiring_review": needs_review,
        "build_verification": before, "saved_verification": after,
        "receipt_path": str(receipt_path), "receipt_sha256": receipt_sha256.lower(),
        "source_path": source_path, "source_sha256": source_sha256,
        "review_report_path": str(report_path), "review_report_sha256": sha256(report_path),
        "preview_scope_reason": reason, "selected_previews": selected,
        "unselected_views": sorted(set(previews) - set(views)),
        "visual_status": "pending_actual_image_review",
        "full_drawing_equivalence": False,
        "additional_inspection": "only_for_new_requirement_changed_identity_or_missing_evidence",
    }


def create_sheet(previews, output):
    from PIL import Image, ImageDraw
    width, height = 640, 480
    rows = (len(previews) + 1) // 2
    sheet = Image.new("RGB", (width * 2, (height + 30) * rows), "white")
    draw = ImageDraw.Draw(sheet)
    for index, preview in enumerate(previews):
        with Image.open(preview["path"]) as raw:
            require(raw.width > 0 and raw.height > 0, "Empty preview")
            thumb = raw.convert("RGB")
            thumb.thumbnail((width, height), Image.Resampling.LANCZOS)
            x, y = (index % 2) * width, (index // 2) * (height + 30)
            draw.text((x + 8, y + 8), preview["view"], fill="black")
            sheet.paste(thumb, (x + (width - thumb.width) // 2, y + 30))
    sheet.save(output)
    return str(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", required=True)
    parser.add_argument("--receipt-sha256", required=True)
    parser.add_argument("--views", nargs="+", required=True)
    parser.add_argument("--reason", required=True)
    parser.add_argument("--source")
    parser.add_argument("--source-sha256")
    parser.add_argument("--output-directory", required=True)
    args = parser.parse_args()
    start = time.perf_counter()
    try:
        result = summarize(args.receipt, args.receipt_sha256, args.views, args.reason,
                           args.source, args.source_sha256)
        output = Path(args.output_directory)
        require(output.is_absolute(), "Use an absolute output directory")
        output.mkdir(parents=True, exist_ok=False)
        result["contact_sheet"] = create_sheet(result["selected_previews"], output / "essential-views.png")
        result["processing_seconds"] = round(time.perf_counter() - start, 4)
        (output / "final-review-summary.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(result, ensure_ascii=False))
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        print(json.dumps({"success": False, "error": str(error)}, ensure_ascii=False))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

