# CLAUDE.md — Synth

Synth — личная RAG-система для кодовых баз + VCS-автоматизации. Стек: **.NET** + **Vue 3/Vite**, **Qdrant** (вектора), конфиг/реестр/граф-вызовов/логи — локально (файл + SQLite, `~/.synth/`; Mongo убран issue #80).

Этот репозиторий на старте — **agent loop**, а не приложение. Само приложение разрабатывается внутри цикла (roadmap ведётся в GitHub issues с меткой `roadmap`).

## Как здесь работать
- Конвенции для агентов — в `AGENTS.md`. Прочитай его перед правками.
- Паттерн цикла — `docs/loop-engineering.md`.
- Один цикл: `scripts/loop.sh` (или команда `/loop`). Роли — `.claude/agents/{maker,checker}.md`, оркестрация — `.claude/skills/loop/SKILL.md`.
- **Валидатор `scripts/validate.sh` — единственный источник правды о правилах приёмки.** Не подменяй его собственным суждением.
- **Roadmap ведётся в GitHub issues с меткой `roadmap`** (один issue на фазу), не в markdown-файле. Обновляй чеклисты в issues по мере продвижения фаз.

## Инварианты
- Одна задача = один worktree = ветка `fix/<task-id>`. Не трогай файлы вне задачи.
- maker и checker независимы (не видят работу друг друга).
- Состояние цикла — только в `loop/state/loop-state.md`; его редактирует оркестратор, не субагенты.
- Код/коммиты — на английском (conventional commits); отчёты человеку — на русском.

## Структура
- `src/` — код приложения: `Synth.Domain/Application/Infrastructure/Api` (слоистая архитектура + CQRS), `src/chunkers/` (5 чанкеров), `src/client/` (Vue).
- `scripts/` — `loop.sh`, `validate.sh`, `new-task.sh`.
- `loop/tasks/` — задачи-контракты; `loop/state/` — память цикла.
