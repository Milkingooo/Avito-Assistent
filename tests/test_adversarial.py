"""«Враждебная модель»: подставляем в разбор ответы, содержащие все ошибки, пойманные на реальной модели,
и проверяем, что защитные слои (ground) приводят условия к тому, что реально сказал покупатель."""
import random
import re
import unittest

from tests.helpers import msgs
from tests.scenarios import S
from tests.runner import run_scenario
from tests.helpers import CATALOG_PATH

from buyer_assistant import extract, heuristic


def model_says(city="unknown", budget=-1, cats=(), flag=False, cons=()):
    def extractor(messages, settings, deadline, log, allow_fallback):
        data = {"city": city, "budget_rub": budget, "categories": list(cats), "unsupported_category": flag,
                "constraints": list(cons)}
        return extract.ground(extract.conditions_from_llm(data), messages, log)
    return extractor


class ReplayedBugs(unittest.TestCase):
    """Каждый тест — реальный провал из живых прогонов."""

    def scenario(self, name):
        return next(s for s in S if s["name"] == name)

    def test_copied_example_values_dont_leak(self):
        # модель выдала бюджет/город из примера промпта вместо «неизвестно»
        sc = self.scenario("бюджет не придуман: стол, кресло без бюджета")
        bad = model_says("taganrog", 15000, ["table", "chair"])
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, bad), [])

    def test_invented_width_from_money(self):
        sc = self.scenario("«до 3000» — это бюджет, а не ширина")
        w = {"attribute": "width_cm", "op": "lte", "value": 3000, "unit": "none"}
        bad = model_says("unknown", -1, ["table"], cons=[w])
        # на втором шаге модель всё ещё путает: город есть, бюджет и ширина перепутаны
        calls = {"n": 0}

        def seq(messages, settings, deadline, log, allow_fallback):
            calls["n"] += 1
            city = "rostov-na-donu" if calls["n"] > 1 else "unknown"
            return model_says(city, -1, ["table"], cons=[w])(messages, settings, deadline, log, allow_fallback)
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, seq), [])

    def test_sticky_unsupported_flag(self):
        sc = self.scenario("после стула называем кресло")
        calls = {"n": 0}

        def sticky(messages, settings, deadline, log, allow_fallback):
            calls["n"] += 1
            return model_says("taganrog", 5000, ["chair"], flag=True)(messages, settings, deadline, log, allow_fallback)
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, sticky), [])

    def test_stool_treated_as_chair_by_model(self):
        sc = self.scenario("стул — неподдерживаемая категория")
        bad = model_says("taganrog", 7000, ["table", "chair"],
                         cons=[{"attribute": "width_cm", "op": "eq", "value": 100, "unit": "cm"}])
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, bad), [])

    def test_stale_budget_after_explicit_change(self):
        sc = self.scenario("смена бюджета")
        stale = model_says("taganrog", 10000, ["table", "chair"])        # модель упорно держит старый бюджет
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, stale), [])

    def test_stale_city_after_explicit_change(self):
        sc = self.scenario("смена города")
        stale = model_says("taganrog", 10000, ["table", "chair"])
        self.assertEqual(run_scenario(sc, CATALOG_PATH, {}, stale), [])


class GroundProperties(unittest.TestCase):
    PIECES = ["стол", "кресло", "лампа", "Таганрог", "в Азове", "Ростов", "до 5000", "за 3 тысячи", "15к", "100 см",
              "не шире метра", "4000 К", "с регулировкой", "стул", "диван", "пятнадцать тысяч", "привет", "ок"]

    def test_random_model_output_never_survives_without_evidence(self):
        rng = random.Random(7)
        for _ in range(1500):
            turns = [" ".join(rng.sample(self.PIECES, rng.randint(1, 4))) for _ in range(rng.randint(1, 3))]
            history = []
            for t in turns:
                history += [{"role": "user", "text": t}, {"role": "assistant", "text": "вопрос"}]
            history.pop()
            cons = [{"attribute": rng.choice(["width_cm", "color_temperature_k", "adjustable_height"]),
                     "op": rng.choice(["lt", "lte", "gt", "gte", "eq"]), "value": rng.choice([1, 100, 4000]),
                     "unit": "none"} for _ in range(rng.randint(0, 2))]
            data = {"city": rng.choice(["taganrog", "azov", "rostov-na-donu", "unknown", "unsupported"]),
                    "budget_rub": rng.choice([-1, 0, 100, 3000, 5000, 7000, 15000]),
                    "categories": rng.sample(["table", "chair", "lamp"], rng.randint(0, 3)),
                    "unsupported_category": rng.random() < .3, "constraints": cons}
            try:
                cond = extract.ground(extract.conditions_from_llm(data), history)
            except extract.ExtractionError:
                continue                                              # такой ответ и так отвергается
            text = " ".join(m["text"] for m in history if m["role"] == "user").lower()
            last = next(m["text"] for m in reversed(history) if m["role"] == "user").lower()
            if cond.city_id:
                self.assertTrue(re.search(heuristic.CITY_STEMS[cond.city_id], text), (turns, cond))
            if cond.budget_rub:
                nums = {int(heuristic._dec(m.group()) * k) for m in re.finditer(heuristic.NUM, text)
                        for k in (1, 1000, 1_000_000)}
                self.assertTrue(cond.budget_rub in nums or extract._NUMBER_WORDS.search(text), (turns, cond))
            for c in cond.constraints:
                if c.attribute == "width_cm":
                    self.assertTrue(extract._LEN_UNIT.search(text), (turns, cond))
                elif c.attribute == "color_temperature_k":
                    self.assertTrue(extract._TEMP_UNIT.search(text), (turns, cond))
                else:
                    self.assertTrue(extract._HEIGHT_WORD.search(text), (turns, cond))
            if heuristic.UNSUPPORTED.search(last):
                self.assertTrue(cond.category_problem, (turns, cond))


class OfflineScenarios(unittest.TestCase):
    def test_all_scenarios_with_rule_parser(self):
        """Резервный парсер обязан проходить все сценарии, не требующие модели."""
        def offline(messages, settings, deadline, log, allow_fallback):
            return heuristic.parse(messages)
        failures = {}
        for sc in S:
            if sc["llm_only"]:
                continue
            errors = run_scenario(sc, CATALOG_PATH, {}, offline)
            if errors:
                failures[sc["name"]] = errors
        self.assertEqual(failures, {})


if __name__ == "__main__":
    unittest.main()
