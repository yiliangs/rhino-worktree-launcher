const bridge = window.chrome?.webview;
const elements = {
    projectTags: document.querySelector("#projectTags"),
    repositoryPath: document.querySelector("#repositoryPath"),
    addProject: document.querySelector("#addProjectButton"),
    refresh: document.querySelector("#refreshButton"),
    refreshIdle: document.querySelector("#refreshIdle"),
    refreshSync: document.querySelector("#refreshSync"),
    localProgress: document.querySelector("#localProgress"),
    gitProgress: document.querySelector("#gitProgress"),
    count: document.querySelector("#worktreeCount"),
    hint: document.querySelector("#panelHint"),
    list: document.querySelector("#worktreeList"),
    empty: document.querySelector("#emptyState"),
    selectedName: document.querySelector("#selectedName"),
    openFolder: document.querySelector("#openFolderButton"),
    launch: document.querySelector("#launchButton")
};

let state = {
    projects: [],
    currentManifestPath: null,
    projectName: "",
    repositoryPath: "",
    worktrees: [],
    selectedPath: null,
    hint: "Double-click to launch",
    syncing: false
};

function post(type, detail = {}) {
    bridge?.postMessage({ type, ...detail });
}

function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
}

function setState(next) {
    state = { ...state, ...next };
    render();
}

function setSync(sync) {
    state.syncing = sync.active;
    elements.refresh.classList.toggle("syncing", sync.active);
    elements.refreshIdle.hidden = sync.active;
    elements.refreshSync.hidden = !sync.active;
    updateProgress(elements.localProgress, sync.local, sync.active);
    updateProgress(elements.gitProgress, sync.git, sync.active);
}

function updateProgress(element, value, active) {
    if (active && value === 0) {
        if (!element.classList.contains("crawling")) {
            element.style.width = "0%";
            requestAnimationFrame(() => {
                element.classList.add("crawling");
                element.style.width = "";
            });
        }
        return;
    }
    element.classList.remove("crawling");
    element.style.width = `${Math.round(value * 100)}%`;
}

function render() {
    renderProjects();
    renderWorktrees();
    const selected = state.worktrees.find(tree => tree.path === state.selectedPath) ?? null;
    elements.selectedName.textContent = selected?.displayName ?? "No worktree selected";
    elements.openFolder.disabled = !selected;
    elements.launch.disabled = !selected?.canLaunch;
    elements.hint.textContent = state.hint || "Double-click to launch";
}

function renderProjects() {
    elements.projectTags.replaceChildren();
    for (const project of state.projects) {
        const tag = document.createElement("button");
        tag.type = "button";
        tag.className = `project-tag${project.manifestPath === state.currentManifestPath ? " active" : ""}`;
        tag.textContent = project.displayName;
        tag.addEventListener("click", () => post("select-project", { manifestPath: project.manifestPath }));
        elements.projectTags.append(tag);
    }
    elements.repositoryPath.textContent = state.repositoryPath || "Add a project to begin";
}

function renderWorktrees() {
    elements.list.replaceChildren();
    elements.count.textContent = String(state.worktrees.length);
    elements.empty.hidden = state.worktrees.length !== 0;
    for (const tree of state.worktrees) {
        elements.list.append(createWorktreeRow(tree));
    }
}

function createWorktreeRow(tree) {
    const row = document.createElement("article");
    row.className = `worktree-row${tree.path === state.selectedPath ? " selected" : ""}`;
    row.tabIndex = 0;
    row.addEventListener("click", () => {
        state.selectedPath = tree.path;
        render();
        post("select", { path: tree.path });
    });
    row.addEventListener("dblclick", () => tree.canLaunch && post("launch", { path: tree.path }));
    row.addEventListener("keydown", event => {
        if (event.key === "Enter" && tree.canLaunch) post("launch", { path: tree.path });
    });

    const identity = document.createElement("div");
    identity.className = "row-identity";
    const name = document.createElement("span");
    name.className = "worktree-name";
    name.textContent = tree.displayName;
    name.title = tree.displayName;
    identity.append(name);
    if (tree.isPrimary) identity.append(makeBadge("DEFAULT", "badge"));
    if (tree.hasPullRequest) identity.append(makePullRequestBadge(tree));

    const rowState = document.createElement("div");
    rowState.className = "row-state";
    const freshness = makeBadge(tree.freshnessLabel, `state-badge${tree.isFresh ? "" : " stale"}`);
    const hasLocalState = tree.hasLocalState !== false;
    const diff = document.createElement("span");
    diff.className = "diff-box";
    diff.append(
        makeMetric(hasLocalState ? `+${tree.localAdded}` : "+…", `added${hasLocalState && tree.localAdded ? "" : " zero"}`),
        makeElement("i", "diff-divider"),
        makeMetric(hasLocalState ? `−${tree.localDeleted}` : "−…", `deleted${hasLocalState && tree.localDeleted ? "" : " zero"}`)
    );
    const activity = makeMetric(hasLocalState ? tree.relativeActivityLabel : "Reading local state…", "activity");
    rowState.append(freshness, spacer(), diff, spacer(), activity, spacer(), makeDivergence(tree));

    row.append(identity, rowState);
    return row;
}

function makeDivergence(tree) {
    const hasGitState = tree.hasGitState !== false;
    const divergence = document.createElement("div");
    divergence.className = "divergence";
    divergence.title = "Compared with the default branch";

    const values = document.createElement("div");
    values.className = "divergence-values";
    values.append(
        makeMetric(hasGitState ? String(tree.behindCount) : "…", `behind-value${hasGitState && tree.behindCount ? "" : " zero"}`),
        makeElement("i", ""),
        makeMetric(hasGitState ? String(tree.aheadCount) : "…", `ahead-value${hasGitState && tree.aheadCount ? "" : " zero"}`)
    );

    const bars = document.createElement("div");
    bars.className = "divergence-bars";
    const behindTrack = makeElement("span", "bar-half behind");
    const behindFill = makeElement("span", "bar-fill behind");
    behindFill.style.width = `${hasGitState ? tree.behindBarWidth : 0}px`;
    behindTrack.append(behindFill);
    const aheadTrack = makeElement("span", "bar-half ahead");
    const aheadFill = makeElement("span", "bar-fill ahead");
    aheadFill.style.width = `${hasGitState ? tree.aheadBarWidth : 0}px`;
    aheadTrack.append(aheadFill);
    bars.append(behindTrack, makeElement("span", "bar-stem"), aheadTrack);

    divergence.append(values, bars);
    return divergence;
}

function makePullRequestBadge(tree) {
    const badge = document.createElement("span");
    badge.className = `pr-badge${tree.isPullRequestDraft ? " draft" : ""}`;
    badge.append(makeElement("i", "pr-dot"), document.createTextNode(tree.pullRequestLabel));
    return badge;
}

function makeBadge(text, className) {
    const badge = document.createElement("span");
    badge.className = className;
    badge.textContent = text;
    return badge;
}

function makeMetric(text, className) {
    const metric = document.createElement("span");
    metric.className = className;
    metric.textContent = text;
    return metric;
}

function makeElement(tag, className) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    return element;
}

function spacer() {
    return makeElement("span", "");
}

elements.refresh.addEventListener("click", () => !state.syncing && post("refresh"));
elements.addProject.addEventListener("click", () => post("add-project"));
elements.openFolder.addEventListener("click", () => state.selectedPath && post("open-folder", { path: state.selectedPath }));
elements.launch.addEventListener("click", () => state.selectedPath && post("launch", { path: state.selectedPath }));

if (bridge) {
    bridge.addEventListener("message", event => {
        const message = event.data;
        if (message.type === "state") setState(message.state);
        if (message.type === "sync") setSync(message);
        if (message.type === "theme") applyTheme(message.theme);
    });
    post("ready");
} else {
    const preview = new URLSearchParams(location.search);
    const forcedTheme = preview.get("theme");
    applyTheme(forcedTheme || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark"));
    setState(mockState());
    if (preview.get("sync") === "1") setSync({ active: true, local: .68, git: .32 });
}

function mockState() {
    const rows = [
        ["feat/435-canonical-suite-search-and-index-rebuild", "16 hours ago", 0, 0, true, true, 214, 63, 435, false],
        ["area-baseline", "23 hours ago", 0, 3, false, false, 0, 0, null, false],
        ["audit-nightscan", "1 month ago", 1, 578, false, false, 41, 12, 402, true],
        ["feat+webview2-warm-preload", "1 month ago", 0, 585, false, false, 1120, 388, null, false],
        ["issue-306-installer-cleanup", "1 month ago", 0, 253, false, false, 6, 74, 306, false],
        ["logging-upgrade", "1 month ago", 0, 460, false, false, 0, 0, null, false],
        ["natalie-ai-layout-morphosis", "2 weeks ago", 20, 523, false, false, 3907, 1244, 418, true],
        ["refactor+fable-revision", "1 month ago", 1, 645, false, false, 88, 90, null, false],
        ["rhino-worktree-launcher-app", "11 hours ago", 6, 3, false, true, 512, 37, 432, false]
    ];
    const cap = Math.max(...rows.flatMap(row => [row[2], row[3]]), 1);
    const width = value => value ? Math.max(3, Math.round(88 * Math.sqrt(value / cap))) : 0;
    const worktrees = rows.map((row, index) => ({
        displayName: row[0], relativeActivityLabel: row[1], aheadCount: row[2], behindCount: row[3],
        isPrimary: row[4], isFresh: row[5], freshnessLabel: row[5] ? "FRESH" : "STALE",
        hasLocalState: true, hasGitState: true,
        localAdded: row[6], localDeleted: row[7], pullRequestNumber: row[8],
        hasPullRequest: row[8] !== null, pullRequestLabel: row[8] ? `PR #${row[8]}` : "",
        isPullRequestDraft: row[9], path: `C:\\worktrees\\${row[0]}`, canLaunch: row[5],
        aheadBarWidth: width(row[2]), behindBarWidth: width(row[3]), selected: index === 0
    }));
    return {
        projects: [{ displayName: "Natalie", manifestPath: "mock" }],
        currentManifestPath: "mock",
        projectName: "Natalie",
        repositoryPath: "C:\\Users\\Yiliang.Shao\\source\\repos\\natalie\\.claude\\worktrees",
        worktrees,
        selectedPath: worktrees[0].path,
        hint: "Double-click to launch"
    };
}
