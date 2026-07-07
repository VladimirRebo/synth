.DEFAULT_GOAL := help

.PHONY: help build test aspire client client-install client-test index loop new-task validate clean

help: ## Show this help
	@grep -hE '^[a-zA-Z_-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*## "}; {printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'

build: ## Build the .NET solution
	dotnet build src/Synth.slnx --nologo

test: ## Run the .NET test suite
	dotnet test src/Synth.slnx --nologo

aspire: ## Run the full local stack (Mongo/Qdrant/Ollama/API) via Aspire — watch the console for the dashboard URL and Synth.Api's port
	cd src && dotnet run --project Synth.AppHost --no-launch-profile

client-install: ## Install the Vue client's dependencies
	cd src/client && npm install

client: client-install ## Run the Vue client dev server (vite)
	cd src/client && npm run dev

client-test: client-install ## Run the Vue client's test suite (vitest)
	cd src/client && npm test

index: ## Index a directory via the running API (needs `make aspire` running elsewhere). Usage: make index DIR=/abs/path PORT=<synth.api-port>
	@test -n "$(DIR)" || (echo "DIR is required, e.g. make index DIR=$$(pwd)/src/Synth.Core PORT=57108" && exit 1)
	@test -n "$(PORT)" || (echo "PORT is required — find Synth.Api's port in the Aspire dashboard" && exit 1)
	curl -sS -X POST http://127.0.0.1:$(PORT)/index -H "Content-Type: application/json" -d '{"path":"$(DIR)"}'

loop: ## Run one agent-loop iteration. Usage: make loop [TASK=SYNTH-n]
	./scripts/loop.sh $(TASK)

new-task: ## Create a new task-contract file. Usage: make new-task NAME="summary" [ID=SYNTH-n]
	@test -n "$(NAME)" || (echo "NAME is required, e.g. make new-task NAME=\"Add /foo endpoint\"" && exit 1)
	./scripts/new-task.sh "$(NAME)" $(ID)

validate: ## Run the deterministic validator for a task. Usage: make validate TASK=SYNTH-n
	@test -n "$(TASK)" || (echo "TASK is required, e.g. make validate TASK=SYNTH-1" && exit 1)
	./scripts/validate.sh $(TASK)

clean: ## Remove build artifacts (bin/obj) across the .NET solution
	find src -type d \( -name bin -o -name obj \) -not -path '*/node_modules/*' -exec rm -rf {} +
