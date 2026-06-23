// sprite_engine/libresprite/ai_bridge.js
// LibreSprite JavaScript Plugin — AI Agent Bridge
//
// INSTALLATION:
//   Copy this file to [LibreSprite installation]/data/scripts/ai_bridge.js
//
// USAGE:
//   1. From Python: write commands to a JSON file (command_queue.json)
//   2. In LibreSprite: Scripts menu > Run "ai_bridge.js"
//   3. Plugin reads queue, executes drawing operations, saves output
//
// LibreSprite JS API reference: see SCRIPTING.md in the repo
// API: app.activeSprite, app.activeImage, image.putPixel(x, y, colorInt32)

var COMMAND_FILE = app.fs.joinPath(app.fs.tempPath, "sprite_engine_queue.json");
var RESULT_FILE  = app.fs.joinPath(app.fs.tempPath, "sprite_engine_result.json");

function colorInt(r, g, b, a) {
    // Returns 32-bit RGBA color integer for LibreSprite's putPixel
    return ((a & 0xFF) << 24) | ((b & 0xFF) << 16) | ((g & 0xFF) << 8) | (r & 0xFF);
}

function executeCommand(cmd, sprite, image) {
    switch (cmd.op) {
        case "put_pixel":
            image.putPixel(cmd.x, cmd.y, colorInt(cmd.r, cmd.g, cmd.b, cmd.a || 255));
            break;

        case "clear":
            var c = cmd.color || {r:0, g:0, b:0, a:0};
            for (var y = 0; y < image.height; y++) {
                for (var x = 0; x < image.width; x++) {
                    image.putPixel(x, y, colorInt(c.r, c.g, c.b, c.a || 255));
                }
            }
            break;

        case "draw_rect":
            var r = cmd.r || 255, g = cmd.g || 0, b = cmd.b || 0, a = cmd.a || 255;
            var col = colorInt(r, g, b, a);
            if (cmd.filled) {
                for (var ry = cmd.y; ry < cmd.y + cmd.h; ry++) {
                    for (var rx = cmd.x; rx < cmd.x + cmd.w; rx++) {
                        image.putPixel(rx, ry, col);
                    }
                }
            } else {
                for (var rx = cmd.x; rx < cmd.x + cmd.w; rx++) {
                    image.putPixel(rx, cmd.y, col);
                    image.putPixel(rx, cmd.y + cmd.h - 1, col);
                }
                for (var ry = cmd.y; ry < cmd.y + cmd.h; ry++) {
                    image.putPixel(cmd.x, ry, col);
                    image.putPixel(cmd.x + cmd.w - 1, ry, col);
                }
            }
            break;

        case "save":
            sprite.saveAs(cmd.path, true);
            break;

        case "resize":
            sprite.width  = cmd.width;
            sprite.height = cmd.height;
            break;

        default:
            console.log("Unknown op: " + cmd.op);
    }
}

function main() {
    // Read command queue
    if (!app.fs.isFile(COMMAND_FILE)) {
        app.alert("No command queue found at:\n" + COMMAND_FILE +
                  "\n\nRun sprite_engine from Python first.");
        return;
    }

    var raw = app.fs.readFile(COMMAND_FILE);
    var queue;
    try {
        queue = JSON.parse(raw);
    } catch (e) {
        app.alert("Failed to parse command queue: " + e);
        return;
    }

    var sprite = app.activeSprite;
    if (!sprite) {
        // Create new sprite if none is open
        if (queue.sprite_width && queue.sprite_height) {
            sprite = new Sprite(queue.sprite_width, queue.sprite_height);
        } else {
            app.alert("No active sprite and no dimensions in queue.");
            return;
        }
    }

    var image = app.activeImage;
    if (!image) {
        app.alert("No active image layer.");
        return;
    }

    // Execute all commands
    var commands = queue.commands || [];
    var results  = [];
    for (var i = 0; i < commands.length; i++) {
        try {
            executeCommand(commands[i], sprite, image);
            results.push({index: i, status: "ok"});
        } catch (e) {
            results.push({index: i, status: "error", message: String(e)});
        }
    }

    // Write results
    var output = JSON.stringify({
        status: "done",
        commands_executed: results.length,
        results: results
    });
    app.fs.writeFile(RESULT_FILE, output);

    sprite.commit && sprite.commit();

    console.log("AI Bridge: Executed " + results.length + " commands.");
}

main();
