"""Прогон сценариев через настоящий конвейер process(); модель подменяется параметром extractor."""
import json
import time

from tests.helpers import ROOT, validate_schema
from tests.validator import check_response

from buyer_assistant.catalog import load_catalog
from buyer_assistant.extract import extract_conditions
from buyer_assistant.pipeline import process


def run_scenario(sc, catalog_path, settings, extractor=extract_conditions, log=lambda *_: None):
    """Возвращает список ошибок сценария (пусто — PASS)."""
    catalog = load_catalog(catalog_path)
    with open(catalog_path, encoding="utf-8") as fh:
        rows = json.load(fh)
    errors, history = [], []
    for i, turn in enumerate(sc["turns"]):
        history.append({"role": "user", "text": turn})
        try:
            resp = process(history, catalog, settings, time.monotonic() + 100, log, True, extractor)
        except Exception as exc:                                    # noqa: BLE001
            return errors + [f"реплика {i + 1}: исключение {type(exc).__name__}: {exc}"]
        try:
            validate_schema(resp)
        except Exception as exc:                                    # noqa: BLE001
            errors.append(f"реплика {i + 1}: ответ нарушает схему: {str(exc)[:120]}")
        want_status, status = sc["expect"][i], resp["status"]
        if want_status == "result":
            if status not in ("ok", "no_match"):
                errors.append(f"реплика {i + 1}: ожидался ok/no_match, получен {status}")
        elif status != want_status:
            errors.append(f"реплика {i + 1}: ожидался {want_status}, получен {status}")
        if status == "clarify":
            fields = [q["field"] for q in resp["questions"]]
            if len(fields) != len(set(fields)):
                errors.append(f"реплика {i + 1}: повтор field в вопросах")
            if i in sc["fields"] and set(fields) != sc["fields"][i]:
                errors.append(f"реплика {i + 1}: поля вопросов {sorted(fields)} != {sorted(sc['fields'][i])}")
            reply = " ".join(q["text"] for q in resp["questions"])
        elif status == "no_match":
            reply = resp["message"]
        else:
            reply = "Варианты: " + "; ".join(",".join(o["listing_ids"]) for o in resp["options"])
        if i == len(sc["turns"]) - 1 and sc["want"] and status in ("ok", "no_match"):
            by_keywords = status == "ok" and "по ключевым словам" in resp["options"][0]["explanation"]
            errors += [f"итог: {e}" for e in check_response(resp, rows, sc["want"], cheapest_first=not by_keywords)]
        history.append({"role": "assistant", "text": reply})
    return errors
