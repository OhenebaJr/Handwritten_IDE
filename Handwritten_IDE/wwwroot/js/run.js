// Run button: opens a WebSocket to /ws/run, shows the program output as it arrives
// and sends the lines typed into the input box back to the running program.
(function () {
    const runBtn = document.getElementById('runBtn');
    const output = document.getElementById('output');
    const inputRow = document.getElementById('inputRow');
    const inputBox = document.getElementById('inputBox');
    const inputSend = document.getElementById('inputSend');

    let socket = null;

    runBtn.addEventListener('click', () => {
        const code = editorApi.getCode();
        if (!code.trim()) {
            output.textContent = 'Nothing to run.';
            return;
        }
        if (socket) socket.close();   // abandon a previous run that is still waiting
        startRun(code);
    });

    function startRun(code) {
        output.textContent = '';
        inputBox.value = '';
        inputBox.placeholder = 'Type input for the program and press Enter';
        inputRow.hidden = false;

        const proto = location.protocol === 'https:' ? 'wss' : 'ws';
        const ws = new WebSocket(`${proto}://${location.host}/ws/run`);
        socket = ws;

        ws.onopen = () => ws.send(JSON.stringify({ type: 'run', code }));

        ws.onmessage = e => {
            if (socket !== ws) return;   // message from an abandoned run
            handle(JSON.parse(e.data), ws);
        };

        ws.onerror = () => {
            if (socket === ws) append('\n[connection error]');
        };

        ws.onclose = () => {
            if (socket === ws) {
                socket = null;
                inputRow.hidden = true;
            }
        };
    }

    function handle(m, ws) {
        switch (m.type) {
            case 'output':
                append(m.text);
                break;
            case 'input-request':
                inputBox.placeholder = 'The program is waiting for input';
                inputBox.focus();
                break;
            case 'finished':
                showResult(m);
                inputRow.hidden = true;
                ws.close();
                break;
        }
    }

    function showResult(m) {
        if (m.status === 'compile-error') {
            const lines = (m.diagnostics || [])
                .filter(d => d.severity === 'Error')
                .map(d => `Line ${d.startLine}: ${d.id}: ${d.message}`);
            output.textContent = 'Compilation failed:\n' + (m.error ? m.error + '\n' : '') + lines.join('\n');
            return;
        }
        if (m.truncated) append('\n[Output truncated]');
        if (m.error) append(`\n[${m.status}] ${m.error}`);
        if (m.status === 'ok' && !output.textContent) {
            output.textContent = '(program finished with no output)';
        }
    }

    function append(text) {
        output.textContent += text;
        output.scrollTop = output.scrollHeight;
    }

    function sendInput() {
        if (!socket || socket.readyState !== WebSocket.OPEN) return;
        const text = inputBox.value;
        append(text + '\n');   // echo the typed line like a console does
        socket.send(JSON.stringify({ type: 'input', text }));
        inputBox.value = '';
        inputBox.placeholder = 'Type input for the program and press Enter';
    }

    inputSend.addEventListener('click', sendInput);
    inputBox.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            e.preventDefault();
            sendInput();
        }
    });
})();