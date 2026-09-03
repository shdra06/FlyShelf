const fs = require('fs');

let extract = fs.readFileSync('components/pdf/ExtractTool.tsx', 'utf8');
extract = extract.replace('const PickButton = ({ label, onPress }: { label: string; onPress: () => void }) => (', 'const PickButton = ({ label, onPress, s }: { label: string; onPress: () => void; s: any }) => (');
extract = extract.replace('<PickButton label="Pick PDF" onPress={handlePick} />', '<PickButton label="Pick PDF" onPress={handlePick} s={s} />');
fs.writeFileSync('components/pdf/ExtractTool.tsx', extract);

let info = fs.readFileSync('components/pdf/InfoTool.tsx', 'utf8');
info = info.replace('const InfoRow = ({ label, value }: { label: string; value: string }) => (', 'const InfoRow = ({ label, value, s }: { label: string; value: string; s: any }) => (');
info = info.replace(/<InfoRow /g, '<InfoRow s={s} ');
fs.writeFileSync('components/pdf/InfoTool.tsx', info);
