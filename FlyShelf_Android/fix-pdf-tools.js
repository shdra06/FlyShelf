const fs = require('fs');
const path = require('path');
const dir = 'E:\\exeapps\\FlyShelf\\FlyShelf_Android\\components\\pdf';
const files = fs.readdirSync(dir).filter(f => f.endsWith('.tsx'));

for (const file of files) {
  const filePath = path.join(dir, file);
  let content = fs.readFileSync(filePath, 'utf8');

  let changed = false;

  // 1. Replace colors import from theme
  if (content.includes('import { colors } from \'../../styles/theme\';')) {
    content = content.replace('import { colors } from \'../../styles/theme\';', '');
    changed = true;
  } else if (content.includes('import { colors, space, radius } from \'../../styles/theme\';')) {
    content = content.replace('import { colors, space, radius } from \'../../styles/theme\';', 'import { space, radius } from \'../../styles/theme\';');
    changed = true;
  } else if (content.includes('import { colors, font } from \'../../styles/theme\';')) {
    content = content.replace('import { colors, font } from \'../../styles/theme\';', 'import { font } from \'../../styles/theme\';');
    changed = true;
  }

  // 2. Change s import
  if (content.includes('import s from \'../../styles/pdfToolsStyles\';')) {
    content = content.replace('import s from \'../../styles/pdfToolsStyles\';', 'import { createPdfToolsStyles } from \'../../styles/pdfToolsStyles\';');
    changed = true;
  }

  // 3. Add useAppTheme and useMemo import if needed
  if (changed && !content.includes('useAppTheme')) {
    content = content.replace(/^import /m, 'import { useAppTheme } from \'../../hooks/useAppTheme\';\nimport ');
    if (!content.includes('useMemo')) {
       // A quick hack to inject useMemo into React import if not there
       if (content.includes('import React, { useState } from \'react\'')) {
         content = content.replace('import React, { useState } from \'react\'', 'import React, { useState, useMemo } from \'react\'');
       } else if (content.includes('import React, { useState, useEffect }')) {
         content = content.replace('import React, { useState, useEffect }', 'import React, { useState, useEffect, useMemo }');
       } else if (content.includes('import React, { useState, useEffect, useRef }')) {
         content = content.replace('import React, { useState, useEffect, useRef }', 'import React, { useState, useEffect, useRef, useMemo }');
       } else if (content.includes('import React from \'react\';')) {
         content = content.replace('import React from \'react\';', 'import React, { useMemo } from \'react\';');
       } else if (content.match(/import React, \{[^\}]+\} from 'react'/)) {
         content = content.replace(/(import React, \{[^\}]+)(\} from 'react')/, '$1, useMemo $2');
       }
    }
  }

  // 4. Inject s and colors inside component
  if (changed) {
    const fnMatch = content.match(/export default function ([A-Za-z0-9_]+)\s*\([^)]*\)\s*\{/);
    if (fnMatch) {
       const insertIndex = fnMatch.index + fnMatch[0].length;
       const injection = '\n  const { colors, shadows } = useAppTheme();' + 
           (content.includes('createPdfToolsStyles') ? '\n  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);' : '') + '\n';
       content = content.slice(0, insertIndex) + injection + content.slice(insertIndex);
    }
  }

  if (changed) {
    fs.writeFileSync(filePath, content);
    console.log('Updated ' + file);
  }
}
