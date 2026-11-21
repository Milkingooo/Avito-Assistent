"""Независимый от matcher.py проверяющий контракта (раздел 4 CONTRACT.md).
Работает с «сырыми» словарями каталога и ответа, на Fraction — без общего кода с решением."""
import itertools
from fractions import Fraction

MISSING = object()
ATTR_CAT = {"width_cm": "table", "adjustable_height": "chair", "color_temperature_k": "lamp"}


def _cmp(op, a, b):
    return {"lt": a < b, "lte": a <= b, "gt": a > b, "gte": a >= b, "eq": a == b}[op]


def sat(row, cons):
    for attr, op, want in cons:
        if ATTR_CAT[attr] != row["category"]:
            continue
        v = row["attributes"].get(attr, MISSING)
        if v is MISSING:
            return False
        if attr == "adjustable_height":
            if v is not want:
                return False
        else:
            if isinstance(v, bool) or not isinstance(v, (int, float)):
                return False
            if not _cmp(op, Fraction(str(v)), Fraction(str(want))):
                return False
    return True


def valid_sets(rows, want):
    per = [[r for r in rows if r["category"] == c and r["city_id"] == want["city"] and sat(r, want["cons"])]
           for c in want["cats"]]
    return [combo for combo in itertools.product(*per) if sum(r["price_rub"] for r in combo) <= want["budget"]]


def check_response(resp, rows, want):
    """want = {"city","budget","cats","cons"}; возвращает список ошибок (пусто — всё верно)."""
    errors = []
    by_id = {r["id"]: r for r in rows}
    allowed = valid_sets(rows, want)
    if resp["status"] == "ok":
        opts = resp["options"]
        if not 1 <= len(opts) <= 3:
            errors.append("options: должно быть от 1 до 3")
        seen = set()
        for opt in opts:
            ids = opt["listing_ids"]
            if len(set(ids)) != len(ids):
                errors.append(f"повтор ID в варианте {ids}")
            if any(i not in by_id for i in ids):
                errors.append(f"несуществующий ID в {ids}")
                continue
            items = [by_id[i] for i in ids]
            if sorted(r["category"] for r in items) != sorted(want["cats"]):
                errors.append(f"состав категорий {ids} не совпадает с запросом {want['cats']}")
            for r in items:
                if r["city_id"] != want["city"]:
                    errors.append(f"{r['id']}: чужой город {r['city_id']}")
                if not sat(r, want["cons"]):
                    errors.append(f"{r['id']}: не удовлетворяет ограничениям")
            total = sum(r["price_rub"] for r in items)
            if opt["total_rub"] != total:
                errors.append(f"total_rub {opt['total_rub']} != {total}")
            if total > want["budget"]:
                errors.append(f"total {total} > бюджета {want['budget']}")
            if not str(opt["explanation"]).strip():
                errors.append("пустое explanation")
            key = tuple(sorted(ids))
            if key in seen:
                errors.append(f"варианты с одинаковым составом {key}")
            seen.add(key)
        if not allowed:
            errors.append("ok, хотя допустимых комплектов нет")
        elif opts and opts[0]["total_rub"] != min(sum(r["price_rub"] for r in c) for c in allowed):
            errors.append("первый вариант не самый дешёвый (возможно, потеряны подходящие товары)")
    elif resp["status"] == "no_match":
        if allowed:
            errors.append(f"no_match, хотя допустимых комплектов {len(allowed)}")
        if "options" in resp or "listing_ids" in resp or not resp.get("message", "").strip():
            errors.append("no_match: лишние поля или пустое message")
    return errors
