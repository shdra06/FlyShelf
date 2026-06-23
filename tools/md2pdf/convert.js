const fs = require('fs');
const path = require('path');
const { marked } = require('marked');
const Prism = require('prismjs');
const puppeteer = require('puppeteer-core');

// Load Prism components dynamically to support languages like C++, C#, Java, Python, Go, Rust, SQL, etc.
const loadLanguages = require('prismjs/components/');

// Load some common programming languages by default to ensure offline readiness
try {
  loadLanguages(['c', 'cpp', 'csharp', 'java', 'python', 'go', 'rust', 'javascript', 'typescript', 'css', 'json', 'bash', 'yaml', 'sql', 'html']);
} catch (e) {
  console.warn('Warning: Failed to pre-load some syntax languages:', e.message);
}

// 1. Configure Marked with PrismJS Syntax Highlighting
const renderer = {
  code(code, infostring) {
    const lang = (infostring || '').match(/\S*/)[0].toLowerCase();
    let highlighted = code;
    
    if (lang) {
      try {
        if (!Prism.languages[lang]) {
          try {
            loadLanguages([lang]);
          } catch (e) {
            // Keep default plain text if language definition can't be loaded
          }
        }
        
        if (Prism.languages[lang]) {
          highlighted = Prism.highlight(code, Prism.languages[lang], lang);
        }
      } catch (err) {
        console.warn(`Highlighting failed for language "${lang}":`, err.message);
      }
    }
    
    const cleanLang = lang ? `language-${lang}` : '';
    return `<pre class="${cleanLang}"><code class="${cleanLang}">${highlighted}</code></pre>`;
  }
};

marked.use({ renderer });

// 2. Locate local Chrome/Edge browser on Windows to avoid download footprint (0MB download)
function locateBrowser() {
  const standardPaths = [
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    path.join(process.env.LOCALAPPDATA || '', 'Google\\Chrome\\Application\\chrome.exe'),
    path.join(process.env.LOCALAPPDATA || '', 'Microsoft\\Edge\\Application\\msedge.exe')
  ];

  for (const executablePath of standardPaths) {
    if (fs.existsSync(executablePath)) {
      return executablePath;
    }
  }
  return null;
}

// 3. Premium CSS stylesheet to match VS Code dark theme look
const themeCSS = `
:root {
  --bg-primary: #0f172a;      /* Slate 900 */
  --bg-card: #1e293b;         /* Slate 800 */
  --text-primary: #f8fafc;    /* Slate 50 */
  --text-secondary: #94a3b8;  /* Slate 400 */
  --accent: #38bdf8;          /* Sky Blue 400 */
  --border: #334155;          /* Slate 700 */
  --accent-translucent: rgba(56, 189, 248, 0.08);
}

body {
  font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, Helvetica, Arial, sans-serif;
  background-color: var(--bg-primary);
  color: var(--text-primary);
  line-height: 1.6;
  padding: 40px;
  margin: 0;
  -webkit-print-color-adjust: exact;
}

h1, h2, h3, h4, h5, h6 {
  color: var(--text-primary);
  font-weight: 600;
  margin-top: 1.5em;
  margin-bottom: 0.5em;
}

h1 {
  font-size: 2em;
  border-bottom: 2px solid var(--border);
  padding-bottom: 0.3em;
}

h2 {
  font-size: 1.5em;
  border-bottom: 1px solid var(--border);
  padding-bottom: 0.3em;
}

p, ul, ol {
  margin-bottom: 1em;
}

a {
  color: var(--accent);
  text-decoration: none;
}

a:hover {
  text-decoration: underline;
}

/* Code block styles to look exactly like modern IDEs */
pre {
  background-color: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 16px;
  margin: 1.5em 0;
  overflow-x: auto;
  box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.2);
}

code {
  font-family: 'Consolas', 'Cascadia Code', 'Fira Code', 'Courier New', monospace;
  font-size: 0.9em;
  background-color: var(--bg-card);
  padding: 2px 6px;
  border-radius: 4px;
}

pre code {
  padding: 0;
  background-color: transparent;
  font-size: 0.9em;
}

/* Prism syntax styling matching a Dracula/OneDark theme */
.token.comment, .token.prolog, .token.doctype, .token.cdata { color: #6272a4; font-style: italic; }
.token.punctuation { color: #f8f8f2; }
.token.property, .token.tag, .token.constant, .token.symbol, .token.deleted { color: #ff79c6; }
.token.boolean, .token.number { color: #bd93f9; }
.token.selector, .token.attr-name, .token.string, .token.char, .token.builtin, .token.inserted { color: #f1fa8c; }
.token.operator, .token.entity, .token.url, .language-css .token.string, .style .token.string { color: #f8f8f2; }
.token.atrule, .token.attr-value, .token.keyword { color: #8be9fd; }
.token.function, .token.class-name { color: #50fa7b; }
.token.regex, .token.important, .token.variable { color: #ffb86c; }

blockquote {
  border-left: 4px solid var(--accent);
  background-color: var(--accent-translucent);
  padding: 12px 20px;
  margin: 1.5em 0;
  color: var(--text-secondary);
  border-radius: 0 8px 8px 0;
}

table {
  width: 100%;
  border-collapse: collapse;
  margin: 1.5em 0;
}

th, td {
  border: 1px solid var(--border);
  padding: 10px 14px;
}

th {
  background-color: var(--bg-card);
  font-weight: 600;
  text-align: left;
}

tr:nth-child(even) {
  background-color: rgba(30, 41, 59, 0.3);
}

img {
  max-width: 100%;
  border-radius: 6px;
  border: 1px solid var(--border);
  margin: 1em 0;
}

@media print {
  pre, code, blockquote, table, tr {
    page-break-inside: avoid;
  }
  h1, h2, h3 {
    page-break-after: avoid;
  }
}
`;

// 4. Main Conversion Invocation
async function run() {
  const args = process.argv.slice(2);
  if (args.length < 2) {
    console.error('Usage: node convert.js <input.md> <output.pdf>');
    process.exit(1);
  }

  const inputPath = path.resolve(args[0]);
  const outputPath = path.resolve(args[1]);

  if (!fs.existsSync(inputPath)) {
    console.error(`Error: Input file does not exist at ${inputPath}`);
    process.exit(1);
  }

  const browserPath = locateBrowser();
  if (!browserPath) {
    console.error('Error: Could not locate a local Chrome or Edge installation.');
    process.exit(2);
  }

  try {
    const mdContent = fs.readFileSync(inputPath, 'utf8');
    const parsedHtml = marked.parse(mdContent);

    const fullHtml = `
      <!DOCTYPE html>
      <html>
      <head>
        <meta charset="utf-8">
        <style>
          ${themeCSS}
        </style>
      </head>
      <body>
        ${parsedHtml}
      </body>
      </html>
    `;

    const browser = await puppeteer.launch({
      executablePath: browserPath,
      headless: 'new',
      args: ['--no-sandbox', '--disable-setuid-sandbox']
    });

    const page = await browser.newPage();
    await page.setContent(fullHtml, { waitUntil: 'networkidle0' });

    // Generate the PDF file via Chromium headless printing
    await page.pdf({
      path: outputPath,
      format: 'A4',
      margin: {
        top: '20mm',
        bottom: '20mm',
        left: '15mm',
        right: '15mm'
      },
      displayHeaderFooter: true,
      headerTemplate: '<span></span>',
      footerTemplate: `
        <div style="font-size: 9px; color: #94a3b8; font-family: sans-serif; width: 100%; text-align: right; padding-right: 25px;">
          Page <span class="pageNumber"></span> of <span class="totalPages"></span>
        </div>
      `,
      printBackground: true
    });

    await browser.close();
    console.log('Success: Markdown successfully converted to PDF.');
  } catch (error) {
    console.error('Conversion execution error:', error.message);
    process.exit(3);
  }
}

run();
