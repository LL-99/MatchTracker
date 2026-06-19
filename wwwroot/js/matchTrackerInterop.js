window.matchTrackerInterop = window.matchTrackerInterop || {};

window.matchTrackerInterop.downloadJsonFile = (fileName, jsonPayload) => {
    const blob = new Blob([jsonPayload], { type: "application/json;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    setTimeout(() => URL.revokeObjectURL(url), 0);
};

window.matchTrackerInterop.pickJsonFile = () =>
    new Promise((resolve) => {
        const input = document.createElement("input");
        input.type = "file";
        input.accept = ".json,application/json";
        input.style.display = "none";

        let isSettled = false;
        let focusFallbackId = null;
        const settle = (value) => {
            if (isSettled) {
                return;
            }

            isSettled = true;
            if (focusFallbackId) {
                window.clearTimeout(focusFallbackId);
            }

            window.removeEventListener("focus", onFocus);
            if (input.parentNode) {
                input.parentNode.removeChild(input);
            }
            resolve(value);
        };

        const onFocus = () => {
            if (focusFallbackId) {
                window.clearTimeout(focusFallbackId);
            }

            focusFallbackId = window.setTimeout(() => {
                if (!isSettled && document.hasFocus() && (!input.files || input.files.length === 0)) {
                    settle(null);
                }
            }, 5000);
        };

        input.addEventListener(
            "change",
            () => {
                const file = input.files && input.files[0];
                if (!file) {
                    settle(null);
                    return;
                }

                const reader = new FileReader();
                reader.onload = () => settle(typeof reader.result === "string" ? reader.result : null);
                reader.onerror = () => settle(null);
                reader.readAsText(file);
            },
            { once: true }
        );
        input.addEventListener("cancel", () => settle(null), { once: true });

        window.addEventListener("focus", onFocus);
        document.body.appendChild(input);
        input.click();
    });
