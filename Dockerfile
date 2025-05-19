FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

WORKDIR /app

COPY . .

RUN uv venv --python 3.12 app
RUN uv build

CMD ["uv", "run", "zendesk"]