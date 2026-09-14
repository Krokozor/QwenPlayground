#!/usr/bin/env python3
"""
Minimal MCP test server (stdio transport).
Exposes 3 tools: echo, add, system_info.
Speaks JSON-RPC 2.0 over stdin/stdout.
"""
import json
import sys
import platform
import os

def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()

def handle_request(req):
    method = req.get("method", "")
    params = req.get("params", {})
    req_id = req.get("id")

    # Notifications (no id) — just ack
    if req_id is None:
        return

    if method == "initialize":
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "test-mcp", "version": "1.0"}
            }
        })
    elif method == "tools/list":
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "tools": [
                    {
                        "name": "echo",
                        "description": "Echo back the input text. Useful for testing.",
                        "inputSchema": {
                            "type": "object",
                            "properties": {
                                "text": {"type": "string", "description": "Text to echo"}
                            },
                            "required": ["text"]
                        }
                    },
                    {
                        "name": "add",
                        "description": "Add two numbers together.",
                        "inputSchema": {
                            "type": "object",
                            "properties": {
                                "a": {"type": "number", "description": "First number"},
                                "b": {"type": "number", "description": "Second number"}
                            },
                            "required": ["a", "b"]
                        }
                    },
                    {
                        "name": "system_info",
                        "description": "Get system information (OS, Python version, cwd).",
                        "inputSchema": {
                            "type": "object",
                            "properties": {}
                        }
                    }
                ]
            }
        })
    elif method == "tools/call":
        tool_name = params.get("name", "")
        args = params.get("arguments", {})

        if tool_name == "echo":
            text = args.get("text", "")
            result_text = f"You said: {text}"
        elif tool_name == "add":
            a = args.get("a", 0)
            b = args.get("b", 0)
            result_text = f"{a} + {b} = {a + b}"
        elif tool_name == "system_info":
            result_text = (
                f"OS: {platform.system()} {platform.release()}\n"
                f"Python: {platform.python_version()}\n"
                f"CWD: {os.getcwd()}\n"
                f"Machine: {platform.machine()}"
            )
        else:
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "error": {"code": -32601, "message": f"Unknown tool: {tool_name}"}
            })
            return

        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "content": [{"type": "text", "text": result_text}],
                "isError": False
            }
        })
    else:
        if req_id is not None:
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "error": {"code": -32601, "message": f"Method not found: {method}"}
            })

def main():
    print("test-mcp server started (stdio)", file=sys.stderr)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            handle_request(req)
        except json.JSONDecodeError as e:
            print(f"JSON error: {e}", file=sys.stderr)
        except Exception as e:
            print(f"Error: {e}", file=sys.stderr)

if __name__ == "__main__":
    main()
