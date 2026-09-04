import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { View, Text, TextInput, StyleSheet, ScrollView, TouchableOpacity, Platform, Alert, KeyboardAvoidingView, Switch } from 'react-native';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';
import * as Haptics from 'expo-haptics';
import * as Clipboard from 'expo-clipboard';
import * as Crypto from 'expo-crypto';
import { Ionicons } from '@expo/vector-icons';
import Animated, { FadeInDown } from 'react-native-reanimated';

interface TextToolsSuiteProps {
  onBack: () => void;
}

const TOOLS = [
  'Word Counter', 'Case Converter', 'Find & Replace', 'Text Diff',
  'Lorem Ipsum', 'Hash Generator', 'JSON Formatter', 'Base64', 'URL Encode'
];

const LOREM_WORDS = ['lorem', 'ipsum', 'dolor', 'sit', 'amet', 'consectetur', 'adipiscing', 'elit', 'sed', 'do', 'eiusmod', 'tempor', 'incididunt', 'ut', 'labore', 'et', 'dolore', 'magna', 'aliqua', 'enim', 'ad', 'minim', 'veniam', 'quis', 'nostrud', 'exercitation', 'ullamco', 'laboris', 'nisi', 'aliquip', 'ex', 'ea', 'commodo', 'consequat', 'duis', 'aute', 'irure', 'in', 'reprehenderit', 'voluptate', 'velit', 'esse', 'cillum', 'fugiat', 'nulla', 'pariatur', 'excepteur', 'sint', 'occaecat', 'cupidatat', 'non', 'proident', 'sunt', 'culpa', 'qui', 'officia', 'deserunt', 'mollit', 'anim', 'id', 'est', 'laborum'];

export const TextToolsSuite: React.FC<TextToolsSuiteProps> = ({ onBack }) => {
  const { colors, shadows } = useAppTheme();
  const [activeTool, setActiveTool] = useState(TOOLS[0]);

  // General states
  const [input1, setInput1] = useState('');
  const [input2, setInput2] = useState('');
  const [input3, setInput3] = useState('');
  const [output, setOutput] = useState('');
  
  // Diff tool
  const [diffResult, setDiffResult] = useState<{type: 'add'|'remove'|'same', text: string}[]>([]);

  // Find & Replace
  const [useRegex, setUseRegex] = useState(false);
  const [matchCount, setMatchCount] = useState(0);

  // Lorem
  const [loremType, setLoremType] = useState<'words'|'sentences'|'paragraphs'>('paragraphs');
  const [loremCount, setLoremCount] = useState('3');

  // JSON
  const [jsonIndent, setJsonIndent] = useState(2);
  const [jsonError, setJsonError] = useState('');

  // Hashes
  const [hashes, setHashes] = useState<{md5: string, sha256: string}>({md5: '', sha256: ''});

  const styles = useMemo(() => StyleSheet.create({
    container: { flex: 1, backgroundColor: colors.bg.base },
    header: { flexDirection: 'row', alignItems: 'center', padding: space.md, backgroundColor: colors.bg.elevated, borderBottomWidth: 1, borderBottomColor: colors.border.subtle },
    backBtn: { padding: space.sm, marginRight: space.sm },
    title: { fontSize: 20, fontFamily: font.bold, color: colors.text.primary },
    toolsScroll: { flexGrow: 0, borderBottomWidth: 1, borderBottomColor: colors.border.subtle },
    toolsScrollContent: { padding: space.sm, gap: space.sm },
    toolPill: { paddingVertical: space.sm, paddingHorizontal: space.md, borderRadius: radius.pill, backgroundColor: colors.bg.elevated, borderWidth: 1, borderColor: colors.border.subtle },
    toolPillActive: { backgroundColor: colors.accent.primary, borderColor: colors.accent.primary },
    toolPillText: { fontFamily: font.medium, color: colors.text.secondary },
    toolPillTextActive: { color: '#fff' },
    content: { flex: 1, padding: space.md },
    input: { backgroundColor: colors.bg.input, color: colors.text.primary, fontFamily: font.regular, fontSize: 16, padding: space.md, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border.medium, minHeight: 120, textAlignVertical: 'top' },
    monoInput: { fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace' },
    label: { fontFamily: font.medium, color: colors.text.secondary, marginBottom: space.xs, marginTop: space.md },
    statsContainer: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm, marginTop: space.md },
    statBox: { backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, flex: 1, minWidth: '45%' },
    statLabel: { fontFamily: font.regular, color: colors.text.tertiary, fontSize: 12 },
    statValue: { fontFamily: font.bold, color: colors.text.primary, fontSize: 18, marginTop: space.xs },
    row: { flexDirection: 'row', gap: space.sm },
    btn: { backgroundColor: colors.accent.primary, padding: space.md, borderRadius: radius.md, alignItems: 'center', flex: 1, flexDirection: 'row', justifyContent: 'center', gap: space.sm },
    btnSecondary: { backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, alignItems: 'center', flex: 1, borderWidth: 1, borderColor: colors.border.medium },
    btnText: { fontFamily: font.medium, color: '#fff', fontSize: 16 },
    btnTextSec: { fontFamily: font.medium, color: colors.text.primary, fontSize: 16 },
    grid: { flexDirection: 'row', flexWrap: 'wrap', gap: space.sm, marginTop: space.md },
    gridBtn: { width: '48%', backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, alignItems: 'center', borderWidth: 1, borderColor: colors.border.medium },
    outputBox: { backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border.subtle, marginTop: space.md, minHeight: 100 },
    outputText: { color: colors.text.primary, fontFamily: font.regular, fontSize: 16 },
    copyBtn: { position: 'absolute', top: space.sm, right: space.sm, padding: space.xs, backgroundColor: colors.bg.card, borderRadius: radius.sm },
    errorText: { color: colors.accent.error, fontFamily: font.medium, marginTop: space.sm },
    diffAdd: { backgroundColor: 'rgba(16, 185, 129, 0.2)', color: colors.text.primary },
    diffRemove: { backgroundColor: 'rgba(244, 63, 94, 0.2)', color: colors.text.primary },
    diffSame: { color: colors.text.primary },
    badge: { backgroundColor: colors.accent.primary, paddingHorizontal: space.sm, paddingVertical: space.xs, borderRadius: radius.pill, alignSelf: 'flex-start', marginTop: space.sm },
    badgeText: { color: '#fff', fontSize: 12, fontFamily: font.bold }
  }), [colors, shadows]);

  const copyToClipboard = async (text: string) => {
    if (!text) return;
    await Clipboard.setStringAsync(text);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    Alert.alert('Copied!', 'Content copied to clipboard.');
  };

  const resetInputs = () => {
    setInput1(''); setInput2(''); setInput3(''); setOutput(''); setDiffResult([]); setJsonError('');
    setHashes({md5: '', sha256: ''});
  };

  useEffect(() => {
    resetInputs();
  }, [activeTool]);

  // Word Counter Logic
  const getStats = (text: string) => {
    const chars = text.length;
    const charsNoSpaces = text.replace(/\s/g, '').length;
    const words = text.trim() ? text.trim().split(/\s+/).length : 0;
    const sentences = text.trim() ? text.split(/[.!?]+/).filter(Boolean).length : 0;
    const paragraphs = text.trim() ? text.split(/\n\n+/).filter(Boolean).length : 0;
    const readTime = Math.ceil(words / 200);
    const speakTime = Math.ceil(words / 130);
    return { chars, charsNoSpaces, words, sentences, paragraphs, readTime, speakTime };
  };

  // Case Converter Logic
  const convertCase = (type: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    let res = '';
    switch(type) {
      case 'UPPER': res = input1.toUpperCase(); break;
      case 'lower': res = input1.toLowerCase(); break;
      case 'Title': res = input1.replace(/\w\S*/g, (t) => t.charAt(0).toUpperCase() + t.substr(1).toLowerCase()); break;
      case 'camel': res = input1.replace(/(?:^\w|[A-Z]|\b\w)/g, (w, i) => i === 0 ? w.toLowerCase() : w.toUpperCase()).replace(/\s+/g, ''); break;
      case 'snake': res = input1.match(/[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+/g)?.map(x => x.toLowerCase()).join('_') || ''; break;
      case 'kebab': res = input1.match(/[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+/g)?.map(x => x.toLowerCase()).join('-') || ''; break;
    }
    setOutput(res);
  };

  // Find & Replace Logic
  const handleReplace = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    if (!input2) return;
    try {
      let count = 0;
      let res = '';
      if (useRegex) {
        const re = new RegExp(input2, 'g');
        const matches = input1.match(re);
        count = matches ? matches.length : 0;
        res = input1.replace(re, input3);
      } else {
        const split = input1.split(input2);
        count = split.length - 1;
        res = split.join(input3);
      }
      setMatchCount(count);
      setOutput(res);
    } catch (e: any) {
      Alert.alert('Error', e.message);
    }
  };

  // Diff Logic
  const handleDiff = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const lines1 = input1.split('\n');
    const lines2 = input2.split('\n');
    let res: {type: 'add'|'remove'|'same', text: string}[] = [];
    
    // Simple diff
    const max = Math.max(lines1.length, lines2.length);
    for(let i=0; i<max; i++) {
      if (lines1[i] === lines2[i]) {
        if(lines1[i] !== undefined) res.push({type: 'same', text: lines1[i]});
      } else {
        if (lines1[i] !== undefined) res.push({type: 'remove', text: lines1[i]});
        if (lines2[i] !== undefined) res.push({type: 'add', text: lines2[i]});
      }
    }
    setDiffResult(res);
  };

  // Lorem Logic
  const generateLorem = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const count = parseInt(loremCount) || 1;
    let res = '';
    const getRandomWord = () => LOREM_WORDS[Math.floor(Math.random() * LOREM_WORDS.length)];
    const getSentence = () => {
      const len = 5 + Math.floor(Math.random() * 10);
      let s = [];
      for(let i=0; i<len; i++) s.push(getRandomWord());
      s[0] = s[0].charAt(0).toUpperCase() + s[0].slice(1);
      return s.join(' ') + '.';
    };

    if (loremType === 'words') {
      res = Array.from({length: count}).map(getRandomWord).join(' ');
    } else if (loremType === 'sentences') {
      res = Array.from({length: count}).map(getSentence).join(' ');
    } else {
      res = Array.from({length: count}).map(() => 
        Array.from({length: 4 + Math.floor(Math.random() * 4)}).map(getSentence).join(' ')
      ).join('\n\n');
    }
    setOutput(res);
  };

  // Hashes
  const generateHashes = async (text: string) => {
    if (!text) { setHashes({md5:'', sha256:''}); return; }
    try {
      const md5 = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.MD5, text);
      const sha256 = await Crypto.digestStringAsync(Crypto.CryptoDigestAlgorithm.SHA256, text);
      setHashes({md5, sha256});
    } catch(e) {
      console.log(e);
    }
  };

  // JSON Format
  const formatJson = (minify: boolean) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setJsonError('');
    try {
      const parsed = JSON.parse(input1);
      setOutput(minify ? JSON.stringify(parsed) : JSON.stringify(parsed, null, jsonIndent));
    } catch (e: any) {
      setJsonError('Invalid JSON: ' + e.message);
      setOutput('');
    }
  };

  // Base64
  const handleBase64 = (encode: boolean) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      if (encode) {
        setOutput(btoa(unescape(encodeURIComponent(input1))));
      } else {
        setOutput(decodeURIComponent(escape(atob(input1))));
      }
    } catch (e: any) {
      Alert.alert('Error', 'Invalid input for Base64 operation');
    }
  };

  // URL Encode
  const handleUrl = (encode: boolean) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      setOutput(encode ? encodeURIComponent(input1) : decodeURIComponent(input1));
    } catch (e: any) {
      Alert.alert('Error', 'Invalid input for URL operation');
    }
  };

  const renderTool = () => {
    switch (activeTool) {
      case 'Word Counter': {
        const stats = getStats(input1);
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={[styles.input, { height: 200 }]} multiline placeholder="Type or paste your text here..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <TouchableOpacity style={[styles.btnSecondary, { marginTop: space.sm }]} onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); setInput1(''); }}>
              <Text style={styles.btnTextSec}>Clear</Text>
            </TouchableOpacity>
            <View style={styles.statsContainer}>
              <View style={styles.statBox}><Text style={styles.statLabel}>Words</Text><Text style={styles.statValue}>{stats.words}</Text></View>
              <View style={styles.statBox}><Text style={styles.statLabel}>Characters</Text><Text style={styles.statValue}>{stats.chars}</Text></View>
              <View style={styles.statBox}><Text style={styles.statLabel}>Chars (No Spaces)</Text><Text style={styles.statValue}>{stats.charsNoSpaces}</Text></View>
              <View style={styles.statBox}><Text style={styles.statLabel}>Sentences</Text><Text style={styles.statValue}>{stats.sentences}</Text></View>
              <View style={styles.statBox}><Text style={styles.statLabel}>Paragraphs</Text><Text style={styles.statValue}>{stats.paragraphs}</Text></View>
              <View style={styles.statBox}><Text style={styles.statLabel}>Reading Time</Text><Text style={styles.statValue}>~{stats.readTime} min</Text></View>
            </View>
          </Animated.View>
        );
      }
      case 'Case Converter':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={styles.input} multiline placeholder="Input text..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <View style={styles.grid}>
              {[
                { l: 'UPPERCASE', v: 'UPPER' }, { l: 'lowercase', v: 'lower' }, { l: 'Title Case', v: 'Title' },
                { l: 'camelCase', v: 'camel' }, { l: 'snake_case', v: 'snake' }, { l: 'kebab-case', v: 'kebab' }
              ].map(btn => (
                <TouchableOpacity key={btn.v} style={styles.gridBtn} onPress={() => convertCase(btn.v)}>
                  <Text style={styles.btnTextSec}>{btn.l}</Text>
                </TouchableOpacity>
              ))}
            </View>
            {output ? (
              <View style={styles.outputBox}>
                <Text style={styles.outputText}>{output}</Text>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
      case 'Find & Replace':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={styles.input} multiline placeholder="Your text..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <View style={[styles.row, { marginTop: space.md, alignItems: 'center' }]}>
              <TextInput style={[styles.input, { flex: 1, minHeight: 50, padding: space.sm }]} placeholder="Find..." placeholderTextColor={colors.text.tertiary} value={input2} onChangeText={setInput2} />
              <TextInput style={[styles.input, { flex: 1, minHeight: 50, padding: space.sm }]} placeholder="Replace..." placeholderTextColor={colors.text.tertiary} value={input3} onChangeText={setInput3} />
            </View>
            <View style={[styles.row, { marginTop: space.sm, alignItems: 'center', justifyContent: 'space-between' }]}>
              <View style={styles.row}>
                <Switch value={useRegex} onValueChange={setUseRegex} trackColor={{ false: colors.border.medium, true: colors.accent.primary }} />
                <Text style={styles.label}>Use Regex</Text>
              </View>
              <TouchableOpacity style={[styles.btn, { flex: 0, paddingHorizontal: space.xl }]} onPress={handleReplace}>
                <Text style={styles.btnText}>Replace All</Text>
              </TouchableOpacity>
            </View>
            {output ? (
              <View style={styles.outputBox}>
                <View style={styles.badge}><Text style={styles.badgeText}>{matchCount} Matches</Text></View>
                <Text style={[styles.outputText, { marginTop: space.sm }]}>{output}</Text>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
      case 'Text Diff':
        return (
          <Animated.View entering={FadeInDown}>
            <View style={styles.row}>
              <View style={{ flex: 1 }}>
                <Text style={styles.label}>Text A (Original)</Text>
                <TextInput style={[styles.input, styles.monoInput]} multiline value={input1} onChangeText={setInput1} />
              </View>
              <View style={{ flex: 1 }}>
                <Text style={styles.label}>Text B (Modified)</Text>
                <TextInput style={[styles.input, styles.monoInput]} multiline value={input2} onChangeText={setInput2} />
              </View>
            </View>
            <TouchableOpacity style={[styles.btn, { marginTop: space.md }]} onPress={handleDiff}>
              <Text style={styles.btnText}>Compare</Text>
            </TouchableOpacity>
            {diffResult.length > 0 && (
              <View style={[styles.outputBox, { marginTop: space.md }]}>
                <ScrollView nestedScrollEnabled style={{ maxHeight: 300 }}>
                  {diffResult.map((line, i) => (
                    <Text key={i} style={[
                      styles.outputText, styles.monoInput,
                      line.type === 'add' ? styles.diffAdd : line.type === 'remove' ? styles.diffRemove : styles.diffSame
                    ]}>
                      {line.type === 'add' ? '+ ' : line.type === 'remove' ? '- ' : '  '}{line.text}
                    </Text>
                  ))}
                </ScrollView>
              </View>
            )}
          </Animated.View>
        );
      case 'Lorem Ipsum':
        return (
          <Animated.View entering={FadeInDown}>
            <View style={styles.row}>
              <View style={{ flex: 1 }}>
                <Text style={styles.label}>Type</Text>
                <View style={styles.row}>
                  {['words', 'sentences', 'paragraphs'].map(t => (
                    <TouchableOpacity key={t} style={[styles.btnSecondary, loremType === t && { borderColor: colors.accent.primary, backgroundColor: colors.accent.primaryDim }]} onPress={() => { Haptics.selectionAsync(); setLoremType(t as any); }}>
                      <Text style={[styles.btnTextSec, { fontSize: 12 }, loremType === t && { color: colors.accent.primary }]}>{t}</Text>
                    </TouchableOpacity>
                  ))}
                </View>
              </View>
              <View style={{ flex: 0.4 }}>
                <Text style={styles.label}>Count</Text>
                <TextInput style={[styles.input, { minHeight: 40, padding: space.sm }]} keyboardType="numeric" value={loremCount} onChangeText={setLoremCount} />
              </View>
            </View>
            <TouchableOpacity style={[styles.btn, { marginTop: space.md }]} onPress={generateLorem}>
              <Text style={styles.btnText}>Generate</Text>
            </TouchableOpacity>
            {output ? (
              <View style={styles.outputBox}>
                <Text style={styles.outputText}>{output}</Text>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
      case 'Hash Generator':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={styles.input} multiline placeholder="Enter text to hash..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={(t) => { setInput1(t); generateHashes(t); }} />
            
            <Text style={styles.label}>MD5</Text>
            <View style={[styles.outputBox, { minHeight: 50, paddingVertical: space.sm, marginTop: 0 }]}>
              <Text style={[styles.outputText, styles.monoInput]} selectable>{hashes.md5}</Text>
              <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(hashes.md5)}>
                <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
              </TouchableOpacity>
            </View>
            
            <Text style={styles.label}>SHA-256</Text>
            <View style={[styles.outputBox, { minHeight: 50, paddingVertical: space.sm, marginTop: 0 }]}>
              <Text style={[styles.outputText, styles.monoInput]} selectable>{hashes.sha256}</Text>
              <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(hashes.sha256)}>
                <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
              </TouchableOpacity>
            </View>
          </Animated.View>
        );
      case 'JSON Formatter':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={[styles.input, styles.monoInput, { height: 200 }]} multiline placeholder="Paste JSON here..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <View style={[styles.row, { marginTop: space.md, alignItems: 'center' }]}>
              <Text style={styles.label}>Indent:</Text>
              {[2, 4].map(ind => (
                <TouchableOpacity key={ind} style={[styles.toolPill, jsonIndent === ind && styles.toolPillActive, { paddingVertical: space.xs }]} onPress={() => { Haptics.selectionAsync(); setJsonIndent(ind); }}>
                  <Text style={[styles.toolPillText, jsonIndent === ind && styles.toolPillTextActive]}>{ind} Spaces</Text>
                </TouchableOpacity>
              ))}
            </View>
            <View style={[styles.row, { marginTop: space.md }]}>
              <TouchableOpacity style={styles.btnSecondary} onPress={() => formatJson(true)}>
                <Text style={styles.btnTextSec}>Minify</Text>
              </TouchableOpacity>
              <TouchableOpacity style={styles.btn} onPress={() => formatJson(false)}>
                <Text style={styles.btnText}>Format</Text>
              </TouchableOpacity>
            </View>
            {jsonError ? <Text style={styles.errorText}>{jsonError}</Text> : null}
            {output ? (
              <View style={styles.outputBox}>
                <ScrollView nestedScrollEnabled style={{ maxHeight: 300 }}>
                  <Text style={[styles.outputText, styles.monoInput]}>{output}</Text>
                </ScrollView>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
      case 'Base64':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={[styles.input, styles.monoInput]} multiline placeholder="Text or Base64..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <View style={[styles.row, { marginTop: space.md }]}>
              <TouchableOpacity style={styles.btn} onPress={() => handleBase64(true)}>
                <Text style={styles.btnText}>Encode</Text>
              </TouchableOpacity>
              <TouchableOpacity style={styles.btnSecondary} onPress={() => handleBase64(false)}>
                <Text style={styles.btnTextSec}>Decode</Text>
              </TouchableOpacity>
            </View>
            {output ? (
              <View style={styles.outputBox}>
                <Text style={[styles.outputText, styles.monoInput]}>{output}</Text>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
      case 'URL Encode':
        return (
          <Animated.View entering={FadeInDown}>
            <TextInput style={[styles.input, styles.monoInput]} multiline placeholder="URL to encode/decode..." placeholderTextColor={colors.text.tertiary} value={input1} onChangeText={setInput1} />
            <View style={[styles.row, { marginTop: space.md }]}>
              <TouchableOpacity style={styles.btn} onPress={() => handleUrl(true)}>
                <Text style={styles.btnText}>Encode</Text>
              </TouchableOpacity>
              <TouchableOpacity style={styles.btnSecondary} onPress={() => handleUrl(false)}>
                <Text style={styles.btnTextSec}>Decode</Text>
              </TouchableOpacity>
            </View>
            {output ? (
              <View style={styles.outputBox}>
                <Text style={[styles.outputText, styles.monoInput]}>{output}</Text>
                <TouchableOpacity style={styles.copyBtn} onPress={() => copyToClipboard(output)}>
                  <Ionicons name="copy-outline" size={20} color={colors.text.secondary} />
                </TouchableOpacity>
              </View>
            ) : null}
          </Animated.View>
        );
    }
  };

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <View style={styles.header}>
        <TouchableOpacity style={styles.backBtn} onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); onBack(); }}>
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </TouchableOpacity>
        <Text style={styles.title}>Text Tools</Text>
      </View>
      <View>
        <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.toolsScroll} contentContainerStyle={styles.toolsScrollContent}>
          {TOOLS.map((tool) => (
            <TouchableOpacity 
              key={tool} 
              style={[styles.toolPill, activeTool === tool && styles.toolPillActive]} 
              onPress={() => {
                Haptics.selectionAsync();
                setActiveTool(tool);
              }}
            >
              <Text style={[styles.toolPillText, activeTool === tool && styles.toolPillTextActive]}>{tool}</Text>
            </TouchableOpacity>
          ))}
        </ScrollView>
      </View>
      <ScrollView style={styles.content} keyboardShouldPersistTaps="handled">
        {renderTool()}
        <View style={{ height: space.xl * 2 }} />
      </ScrollView>
    </KeyboardAvoidingView>
  );
};
