// Run button: sends the recognized code to /api/run and shows the result.
(function () {
    const runBtn = document.getElementById('runBtn');
    const output = document.getElementById('output');

    runBtn.addEventListener('click', async () => {
        const code = editorApi.getCode();
        if (!code.trim()) {
            output.textContent = 'Nothing to run.';
            return;
        }

        runBtn.disabled = true;
        output.textContent = 'Running...';
        try {
            const res = await fetch('/api/run', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            show(await res.json());
        } catch (err) {
            output.textContent = `Run failed: ${err.message}`;
        } finally {
            runBtn.disabled = false;
        }
    });

    function show(r) {
        if (r.status === 'compile-error') {
            const lines = r.diagnostics
                .filter(d => d.severity === 'Error')
                .map(d => `Line ${d.startLine}: ${d.id}: ${d.message}`);
            output.textContent = 'Compilation failed:\n' + lines.join('\n');
            return;
        }

        let text = r.output || '';
        if (r.outputTruncated) text += '\n[Output truncated]';
        if (r.error) text += `\n[${r.status}] ${r.error}`;
        if (r.status === 'ok' && !text) text = '(program finished with no output)';
        output.textContent = text;
    }
})();