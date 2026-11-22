# Band D (D2) - the Durable hosted agent image.
#
# Same base, same user, same port and same health check as the D1 image, because the
# platform requirements are the same ones: the platform mounts the per-session volume at
# HOME=/home/session owned by root, ResponsesHostServer creates $HOME/.sessions during
# startup, and a non-root UID gets EACCES before /readiness binds (surfaced as HTTP 424
# session_not_ready). See ../band-d-hosted-foundry-agent/agent/Dockerfile.
#
# What is different: the Durable package is installed from its source tree (the build
# context carries lib/py as `durable-package/`, assembled by infra/deploy.sh), and the
# image ships the two scripts this scenario runs inside the container - the tool the
# platform invokes, and the filesystem probe.
FROM python:3.12-slim

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

# The Durable layer, from the repository's own source tree at the commit under test.
# It is installed as a module rather than through its sdist metadata: pyproject.toml has
# build-time requirements the base image does not carry, and the layer is a single file
# whose PyPI dependencies (github-copilot-sdk) are already pinned in requirements.txt.
# `pip install <dir>` was tried first and failed in this image with
# `BackendUnavailable: Cannot import 'setuptools.build_meta'`; copying the module is the
# same install without a build backend, and it is what the image needs.
COPY durable-package/ /tmp/durable-package/
RUN cp /tmp/durable-package/durable.py /usr/local/lib/python3.12/site-packages/durable.py && \
    rm -rf /tmp/durable-package

COPY main.py ./
COPY durable_state.py ./
COPY d2_tool.py ./

# Runs as root by design - the platform's session volume is root-owned (see above).
ENV PYTHONUNBUFFERED=1

EXPOSE 8088

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8088/readiness || exit 1

CMD ["python", "main.py"]
