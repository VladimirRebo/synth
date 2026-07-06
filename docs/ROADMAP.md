# Walter — Roadmap

Walter = личный, более продвинутый аналог Sonar. RAG для кодовых баз + VCS-автоматизации.
Стек: **.NET** (бэкенд) + **Vue 3/Vite** (клиент), **Qdrant** (вектора), **MongoDB** (конфиг).
Разработка идёт через agent loop (см. `loop-engineering.md`).

## Фазы

### 0. Agent loop (готово)
Скаффолд цикла: оркестратор, maker/checker, валидатор, состояние, задачи-контракты.

### 1. Каркас бэкенда
- `WALTER-1` — solution + Web API + `/health` (первая задача в очереди).
- Слоирование конфига (перенести идею `config-layering-cicd` из Sonar, но для личного окружения — без Kaspi-специфики).
- MongoDB как config store.

### 2. Индексация и поиск (ядро RAG)
- Модель `CodeChunk`, чанкеры (AST через Roslyn для C#, regex-фолбэк) — из `code-chunking-strategy`.
- Эмбеддинги (провайдер-абстракция; для личного проекта — совместимость с Claude/OpenAI вне Azure).
- Qdrant как векторное хранилище.
- Поиск с ре-ранкингом (`rag-search-reranking`).

### 3. MCP-слой
- MCP-инструменты как библиотека без транспорта (stdio + HTTP) — `dual-transport-mcp`.
- Это же — connector-слой для самого loop (шаг 4 матрицы зрелости).

### 4. VCS-автоматизации
- Вебхуки (`webhook-pipeline-pattern`), переиндексация по push.
- AI-ревью MR/issue.
- Автор ешение issue — переосмысленное через loop engineering: worktree на задачу,
  независимый checker, детерминированный валидатор (уже заложено в этом репозитории).

### 5. Клиент
- Vue 3 + Vite SPA, runtime base-path (`runtime-base-path`).

## Открытые решения
- Оркестрация: свой loop (как сейчас) vs **Microsoft Agent Framework** — оценить
  MCP-поддержку и работу с Claude вне Azure.
- Финальное имя проекта (сейчас `Walter`, кандидат `Synth`).
- До какого уровня матрицы зрелости доводить автономию.

> Паттерны в `[[...]]` — страницы вики знаний в `/Users/vladimir/Jarvis`.
