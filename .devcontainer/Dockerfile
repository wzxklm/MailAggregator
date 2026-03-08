# syntax=docker/dockerfile:1
FROM nvidia/cuda:12.4.0-devel-ubuntu22.04

# ============ 环境变量 ============
ARG TZ
ENV TZ="$TZ"
ENV DEBIAN_FRONTEND=noninteractive
ENV CUDA_HOME=/usr/local/cuda
ENV TORCH_CUDA_ARCH_LIST="8.9"
ENV CONDA_DIR=/opt/conda
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8
ENV DEVCONTAINER=true
ENV SHELL=/bin/bash
ENV EDITOR=nano
ENV VISUAL=nano

# ============ 系统依赖 ============
RUN --mount=type=cache,target=/var/cache/apt \
    --mount=type=cache,target=/var/lib/apt/lists \
    apt-get update && apt-get install -y --no-install-recommends \
    curl \
    wget \
    build-essential \
    ca-certificates \
    less \
    git \
    procps \
    sudo \
    man-db \
    unzip \
    gnupg2 \
    jq \
    nano \
    vim \
    locales \
    && locale-gen en_US.UTF-8

# ============ Node.js + gh CLI ============
RUN --mount=type=cache,target=/var/cache/apt \
    --mount=type=cache,target=/var/lib/apt/lists \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update && apt-get install -y nodejs gh

# ============ Bash 配置 ============
RUN SNIPPET="export PROMPT_COMMAND='history -a' && export HISTFILE=/commandhistory/.bash_history" \
    && mkdir /commandhistory \
    && touch /commandhistory/.bash_history \
    && echo "$SNIPPET" >> /root/.bashrc

# ============ 工作目录 ============
RUN mkdir -p /workspace /root/.claude
WORKDIR /workspace

# ============ git-delta ============
ARG GIT_DELTA_VERSION
RUN ARCH=$(dpkg --print-architecture) && \
    wget "https://github.com/dandavison/delta/releases/download/${GIT_DELTA_VERSION}/git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" && \
    dpkg -i "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb" && \
    rm "git-delta_${GIT_DELTA_VERSION}_${ARCH}.deb"

# ============ Miniconda ============
RUN ARCH=$(uname -m) && \
    wget --quiet "https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-${ARCH}.sh" -O /tmp/miniconda.sh && \
    /bin/bash /tmp/miniconda.sh -b -p $CONDA_DIR && \
    rm /tmp/miniconda.sh && \
    $CONDA_DIR/bin/conda init bash && \
    $CONDA_DIR/bin/conda config --set auto_activate_base false

# ============ Claude Code ============
RUN curl -fsSL https://claude.ai/install.sh | bash
ENV PATH="/root/.local/bin:$PATH"

