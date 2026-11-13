// Monaco editor showing the recognized code (read-only for now).
// capture.js talks to it only through window.editorApi.
// Every time the code changes it is sent to /api/validate and the
// returned Roslyn errors and warnings are shown as markers in the editor.
(function () {
    let editor = null;
    let pendingText = '';
    let validationId = 0;   // bumped on every change so late responses are ignored

    window.editorApi = {
        setLines(lines) {
            pendingText = lines.join('\n');
            if (editor) editor.setValue(pendingText);
            validate(pendingText);
        }
    };

    async function validate(code) {
        const myId = ++validationId;
        if (!editor) return;

        // Remove old markers straight away, they may point to outdated positions
        monaco.editor.setModelMarkers(editor.getModel(), 'roslyn', []);
        if (!code.trim()) return;

        try {
            const res = await fetch('/api/validate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const diagnostics = await res.json();
            if (myId !== validationId) return;   // the code changed while waiting
            showMarkers(diagnostics);
        } catch (err) {
            console.error('Validation failed:', err);
        }
    }

    function showMarkers(diagnostics) {
        const model = editor.getModel();
        const markers = diagnostics.map(d => {
            let startColumn = d.startColumn;
            let endColumn = d.endColumn;

            // Errors such as "; expected" have an empty range, which would be
            // invisible, so widen them to one character.
            if (d.startLine === d.endLine && startColumn === endColumn) {
                const max = model.getLineMaxColumn(d.startLine);
                if (endColumn < max) endColumn++;
                else startColumn = Math.max(1, startColumn - 1);
            }

            return {
                severity: d.severity === 'Error'
                    ? monaco.MarkerSeverity.Error
                    : monaco.MarkerSeverity.Warning,
                message: `${d.id}: ${d.message}`,
                startLineNumber: d.startLine,
                startColumn: startColumn,
                endLineNumber: d.endLine,
                endColumn: endColumn
            };
        });
        monaco.editor.setModelMarkers(model, 'roslyn', markers);
    }

    require.config({
        paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' }
    });

    require(['vs/editor/editor.main'], function () {
        editor = monaco.editor.create(document.getElementById('editor'), {
            value: pendingText,
            language: 'csharp',
            readOnly: false,
            automaticLayout: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            fontSize: 14
        });
        validate(pendingText);
    });
})();