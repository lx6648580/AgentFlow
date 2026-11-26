(() => {
    const preferenceKey = "agentflow.ui.language";
    let database;
    let interop;

    customElements.define("agentflow-log", class extends HTMLElement {
        connectedCallback() {
            let following = true;
            this.trackScroll = () => { following = this.scrollHeight - this.clientHeight - this.scrollTop < 8; };
            this.addEventListener("scroll", this.trackScroll);
            const followTail = () => {
                if (this.dataset.follow === "true" && following) this.scrollTop = this.scrollHeight;
            };
            this.observer = new MutationObserver(followTail);
            this.observer.observe(this, { childList: true, subtree: true, characterData: true });
            followTail();
        }

        disconnectedCallback() {
            this.observer.disconnect();
            this.removeEventListener("scroll", this.trackScroll);
        }
    });

    function draftDatabase() {
        database ??= new Promise((resolve, reject) => {
            const request = indexedDB.open("agentflow-phase00", 1);
            request.onupgradeneeded = () => request.result.createObjectStore("drafts");
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new Error("IndexedDB upgrade blocked"));
        });
        return database;
    }

    async function draftTransaction(mode, operation) {
        const db = await draftDatabase();
        return new Promise((resolve, reject) => {
            const transaction = db.transaction("drafts", mode);
            const request = operation(transaction.objectStore("drafts"));
            transaction.oncomplete = () => resolve(request.result ?? null);
            transaction.onabort = () => reject(transaction.error);
        });
    }

    function bind(canvas, dotnet) {
        interop = dotnet;
        const selection = canvas.querySelector(".selection-box");
        let start, link, resize;
        const editor = canvas.closest(".studio");
        window.addEventListener("languagechange", () => dotnet.invokeMethodAsync("BrowserLanguageChanged"));
        const editable = target => target.closest("input, textarea, select, [contenteditable]");
        editor.addEventListener("keydown", event => {
            if ((!event.ctrlKey && !event.metaKey) || event.altKey || event.isComposing) return;
            const key = event.key.toLowerCase();
            if (editable(event.target) && key !== "s") return;
            if (key === "c" && event.target.closest("[data-run-console], .terminal-event-detail")) return;
            if (key === "c" && event.target.closest("[data-output-copy-area]")) {
                const area = event.target.closest("[data-output-copy-area]");
                const selected = window.getSelection();
                if (selected?.toString() && area.contains(selected.anchorNode)) return;
                event.preventDefault();
                event.stopPropagation();
                area.closest("[data-node]")?.querySelector("[data-output-copy]")?.click();
                return;
            }
            if (key === "o") {
                event.preventDefault();
                editor.querySelector("input[type=file]").click();
                return;
            }
            const commands = { z: event.shiftKey ? "redo" : "undo", y: "redo", c: "copy", v: "paste",
                g: "group", s: "save", b: "bypass", m: "disabled" };
            if (!(key in commands)) return;
            event.preventDefault();
            event.stopPropagation();
            dotnet.invokeMethodAsync("Shortcut", commands[key]);
        }, true);
        editor.addEventListener("dragstart", event => {
            const item = event.target.closest("[data-library]");
            if (!item) return;
            event.dataTransfer.setData("application/x-agentflow-node", item.dataset.library);
            event.dataTransfer.effectAllowed = "copy";
        });
        canvas.addEventListener("dragover", event => {
            if (event.dataTransfer.types.includes("application/x-agentflow-node")) event.preventDefault();
        });
        canvas.addEventListener("drop", event => {
            const type = event.dataTransfer.getData("application/x-agentflow-node");
            if (!type) return;
            event.preventDefault();
            dotnet.invokeMethodAsync("DropFromLibrary", type, event.clientX, event.clientY);
        });
        canvas.addEventListener("contextmenu", event => {
            if (editable(event.target) || event.target.closest(".node-search, .minimap-wrap, [data-run-console], .terminal-event-detail")) return;
            event.preventDefault();
            const model = event.target.closest("[data-node], [data-link-id], [data-group-id]")?.dataset;
            dotnet.invokeMethodAsync("OpenContext", event.clientX, event.clientY,
                model?.node ?? model?.linkId ?? model?.groupId ?? null);
        });
        canvas.addEventListener("pointerdown", event => {
            const handle = event.target.closest("[data-output-resize]");
            if (handle && event.button === 0) {
                const node = handle.closest("[data-node]");
                const width = node.offsetWidth, height = node.offsetHeight;
                resize = { node, width, height, x: event.clientX, y: event.clientY,
                    scale: node.getBoundingClientRect().width / width, pointer: event.pointerId, moved: false };
                handle.setPointerCapture(event.pointerId);
                event.stopPropagation();
                event.preventDefault();
                return;
            }
            const from = event.target.closest(".flow-port")?.closest(".port-row.out");
            link = from && event.button === 0 ? { node: from.closest("[data-node]").dataset.node, port: from.dataset.port,
                x: event.clientX, y: event.clientY } : null;
            if (!event.shiftKey || event.button !== 0 || event.target.closest(".flow-node, .node-search")) return;
            start = { x: event.clientX, y: event.clientY, append: event.ctrlKey || event.metaKey };
            event.stopPropagation();
            event.preventDefault();
            canvas.setPointerCapture(event.pointerId);
        }, true);
        canvas.addEventListener("pointermove", event => {
            if (resize && event.pointerId === resize.pointer) {
                const width = Math.round(Math.max(223, Math.min(1600, resize.width + (event.clientX - resize.x) / resize.scale)));
                const height = Math.round(Math.max(130, Math.min(1200, resize.height + (event.clientY - resize.y) / resize.scale)));
                resize.node.dataset.outputSized = "true";
                resize.node.style.width = width + "px";
                resize.node.style.height = height + "px";
                resize.moved = true;
                return;
            }
            if (!start) return;
            const bounds = canvas.getBoundingClientRect();
            selection.hidden = false;
            Object.assign(selection.style, {
                left: Math.min(start.x, event.clientX) - bounds.left + "px",
                top: Math.min(start.y, event.clientY) - bounds.top + "px",
                width: Math.abs(start.x - event.clientX) + "px",
                height: Math.abs(start.y - event.clientY) + "px"
            });
        });
        canvas.addEventListener("pointerup", event => {
            if (resize && event.pointerId === resize.pointer) {
                const { node, moved } = resize;
                resize = null;
                if (moved) dotnet.invokeMethodAsync("ResizeOutput", node.dataset.node, node.offsetWidth, node.offsetHeight);
                event.stopPropagation();
                return;
            }
            if (link && !document.elementFromPoint(event.clientX, event.clientY)?.closest(".flow-node, .node-search, .minimap-wrap") &&
                Math.hypot(event.clientX - link.x, event.clientY - link.y) > 12) {
                const from = link;
                // 先让画布库清理临时连线，再打开兼容节点搜索。
                setTimeout(() => dotnet.invokeMethodAsync("OpenDownstream", event.clientX, event.clientY, from.node, from.port), 0);
            }
            link = null;
            if (!start) return;
            const bounds = selection.getBoundingClientRect();
            const ids = selection.hidden ? [] : Array.from(canvas.querySelectorAll("[data-node]")).filter(node => {
                const r = node.getBoundingClientRect();
                return r.right > bounds.left && r.left < bounds.right && r.bottom > bounds.top && r.top < bounds.bottom;
            }).map(node => node.dataset.node);
            selection.hidden = true;
            dotnet.invokeMethodAsync("SelectRectangle", ids, start.append);
            start = null;
            event.stopPropagation();
        }, true);
        canvas.addEventListener("lostpointercapture", event => {
            if (resize && event.pointerId === resize.pointer) {
                const { node, moved } = resize;
                resize = null;
                if (moved) dotnet.invokeMethodAsync("ResizeOutput", node.dataset.node, node.offsetWidth, node.offsetHeight);
            }
            start = null;
            selection.hidden = true;
        });
        canvas.addEventListener("keydown", event => {
            const handle = event.target.closest("[data-output-resize]");
            const directions = { ArrowLeft: [-24, 0], ArrowRight: [24, 0], ArrowUp: [0, -24], ArrowDown: [0, 24] };
            if (!handle || !(event.key in directions)) return;
            const [x, y] = directions[event.key];
            const node = handle.closest("[data-node]");
            event.preventDefault();
            event.stopPropagation();
            dotnet.invokeMethodAsync("ResizeOutput", node.dataset.node, node.offsetWidth + x, node.offsetHeight + y);
        }, true);
    }

    window.agentFlow = {
        preference() {
            try { return localStorage.getItem(preferenceKey) ?? "auto"; }
            catch { return "auto"; }
        },
        writePreference(value) {
            try { localStorage.setItem(preferenceKey, value); return true; }
            catch { return false; }
        },
        askName: (title, value) => window.prompt(title, value),
        showModal: dialog => { if (!dialog.open) dialog.showModal(); },
        closeModal: dialog => dialog.close(),
        async copyText(value) {
            try {
                await navigator.clipboard.writeText(value);
                return true;
            } catch {
                return false;
            }
        },
        languages: () => [...(navigator.languages ?? []), navigator.language || "en"],
        setLanguage: value => { document.documentElement.lang = value; },
        readDraft: () => draftTransaction("readonly", store => store.get("current")),
        readRunId: () => sessionStorage.getItem("agentflow.runId"),
        writeRunId: id => sessionStorage.setItem("agentflow.runId", id),
        writeDraft: value => draftTransaction("readwrite", store => store.put(value, "current")),
        readCatalog: url => draftTransaction("readonly", store => store.get("catalog:" + url)),
        writeCatalog: (url, value) => draftTransaction("readwrite", store => store.put(value, "catalog:" + url)),
        download(filename, value, contentType = "application/json") {
            const url = URL.createObjectURL(new Blob([value], { type: contentType }));
            const link = document.createElement("a");
            link.href = url;
            link.download = filename;
            link.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        },
        bind,
        benchmarkUpdate: () => interop.invokeMethodAsync("UpdateBenchmark")
    };
})();
