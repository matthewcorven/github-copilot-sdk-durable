# Band D (D1) — coverage agent image for the hosted deployment.
#
# Byte-for-byte the accelerator's agents/coverage/Dockerfile (vendored at
# upstream/agents/coverage/Dockerfile) except for the build-context layout:
# this context is assembled by deploy.sh from upstream/ (agent source, verbatim)
# plus agent/requirements.txt (the one deliberate change, described in PATCHES.md).
# The comments about root and $HOME are the accelerator's and still apply on the
# platform.
FROM python:3.12-slim

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py ./
COPY schemas.py ./
COPY skills/ ./skills/

# Runs as root by design. Foundry injects HOME=/home/session and mounts the
# per-session state volume there owned by root; ResponsesHostServer creates
# $HOME/.sessions during startup, so a non-root UID gets EACCES and the
# container exits before /readiness binds (surfaces as HTTP 424
# session_not_ready).

ENV PYTHONUNBUFFERED=1

EXPOSE 8088

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8088/readiness || exit 1

CMD ["python", "main.py"]
