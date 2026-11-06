// Monaco editor showing the recognized code (read-only for now).
// capture.js talks to it only through window.editorApi.
(function () {
    let editor = null;
    let pendingText = '';

    window.editorApi = {
        setLines(lines) {
            pendingText = lines.join('\n');
            if (editor) editor.setValue(pendingText);
        }
    };

    require.config({
        paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs' }
    });

    require(['vs/editor/editor.main'], function () {
        editor = monaco.editor.create(document.getElementById('editor'), {
            value: pendingText,
            language: 'csharp',
            readOnly: true,
            automaticLayout: true,
            minimap: { enabled: false },
            scrollBeyondLastLine: false,
            fontSize: 14
        });
    });
})();