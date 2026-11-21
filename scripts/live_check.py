#!/usr/bin/env python3
"""Проверка конвейера на ~40 диалогах. Запуск из корня репозитория:

    python scripts/live_check.py              # с реальной моделью (нужен .env с ключом)
    python scripts/live_check.py --offline    # только резервный парсер правил, без сети
    python scripts/live_check.py -k стул -v   # фильтр по названию, подробный лог

Каждый ответ проверяется независимым валидатором контракта (tests/validator.py): ID существуют и не повторяются,
по одному товару запрошенной категории, город, ограничения, сумма, бюджет, полнота (ok <=> комплект существует,
первый вариант — самый дешёвый). Между вызовами модели пауза, чтобы не упереться в лимит 20 запросов/мин."""
import argparse
import os
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from buyer_assistant import heuristic  # noqa: E402
from buyer_assistant.config import load_settings  # noqa: E402
from tests.runner import run_scenario  # noqa: E402
from tests.scenarios import S  # noqa: E402


def offline_extractor(messages, settings, deadline, log, allow_fallback):
    return heuristic.parse(messages)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--catalog", default=os.path.join(ROOT, "data", "catalog.json"))
    ap.add_argument("--offline", action="store_true")
    ap.add_argument("-k", default="", help="подстрока в названии сценария")
    ap.add_argument("-v", action="store_true")
    ap.add_argument("--pause", type=float, default=2.5, help="пауза между репликами (сек) при работе с моделью")
    args = ap.parse_args()

    settings = load_settings()
    if not args.offline and not (settings["LLM_API_KEY"] and settings["LLM_BASE_URL"]):
        sys.exit("Нет LLM_API_KEY / LLM_BASE_URL (.env). Запустите с --offline или заполните .env")
    log = (lambda *p: print("      [лог]", *p)) if args.v else (lambda *p: None)
    if not args.offline:      # без тихого перехода на правила: хотим видеть именно ответы модели
        os.environ["LLM_FALLBACK"] = "0"
    from buyer_assistant.extract import extract_conditions

    def live_extractor(messages, settings, deadline, log_, allow_fallback):
        time.sleep(args.pause)
        return extract_conditions(messages, settings, deadline, log_, allow_fallback=False)

    extractor = offline_extractor if args.offline else live_extractor
    passed = failed = skipped = 0
    for sc in S:
        if args.k.lower() not in sc["name"].lower():
            continue
        if args.offline and sc["llm_only"]:
            print(f"SKIP  {sc['name']} (нужна модель)")
            skipped += 1
            continue
        errors = run_scenario(sc, args.catalog, settings, extractor, log)
        if errors:
            failed += 1
            print(f"FAIL  {sc['name']}")
            for e in errors:
                print("        -", e)
        else:
            passed += 1
            print(f"PASS  {sc['name']}")
    print(f"\nИтого: {passed} PASS, {failed} FAIL, {skipped} SKIP")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
