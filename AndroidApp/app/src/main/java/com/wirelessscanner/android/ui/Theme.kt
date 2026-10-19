package com.wirelessscanner.android.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** The Windows HUD palette (Themes/HudTheme.xaml) with validated status colours; status always also has a word. */
object Hud {
    val Bg = Color(0xFF0B0705)
    val Panel = Color(0xFF140B05)
    val Panel2 = Color(0xFF1E1108)
    val Border = Color(0xFF4A2A12)
    val BorderStrong = Color(0xFF883300)
    val Accent = Color(0xFFFF8800)
    val Accent2 = Color(0xFFFFAA33)
    val Text = Color(0xFFF3D9B8)
    val Muted = Color(0xFFC9A27A)
    val Dim = Color(0xFF7A6A55)
    val Good = Color(0xFF0CA30C)
    val Warn = Color(0xFFFAB219)
    val Serious = Color(0xFFEC835A)
    val Critical = Color(0xFFD03B3B)
    val Identity = listOf(Color(0xFF3987E5), Color(0xFFD95926), Color(0xFF199E70))
}

fun argb(v: Long) = Color(v.toInt())

@Composable
fun WirelessScannerTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Hud.Accent, onPrimary = Color.Black, secondary = Hud.Accent2, onSecondary = Color.Black, background = Hud.Bg, onBackground = Hud.Text,
            surface = Hud.Panel, onSurface = Hud.Text, surfaceVariant = Hud.Panel2, onSurfaceVariant = Hud.Muted, outline = Hud.BorderStrong, error = Hud.Critical,
            surfaceContainer = Hud.Panel, surfaceContainerHigh = Hud.Panel2, surfaceContainerHighest = Hud.Panel2, surfaceContainerLow = Hud.Panel,
        ),
        content = content,
    )
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) =
    Text(text, color = Hud.Accent, fontWeight = FontWeight.Bold, fontSize = 13.sp, letterSpacing = 1.sp, modifier = modifier.padding(top = 12.dp, bottom = 6.dp))

@Composable
fun Muted(text: String, modifier: Modifier = Modifier, size: Int = 13) = Text(text, color = Hud.Muted, fontSize = size.sp, modifier = modifier)

@Composable
fun Panel(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) =
    Column(modifier.background(Hud.Panel, RoundedCornerShape(8.dp)).border(1.dp, Hud.Border, RoundedCornerShape(8.dp)).padding(10.dp), content = content)

/** Primary action (≥ 48 dp). */
@Composable
fun HudButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, primary: Boolean = false, danger: Boolean = false, onClick: () -> Unit) {
    val bg = when { !enabled -> Hud.Panel2; danger -> Color(0xFF6B1D10); primary -> Hud.Accent; else -> Hud.Panel2 }
    val fg = when { !enabled -> Hud.Dim; primary -> Color.Black; else -> Hud.Text }
    Button(onClick = onClick, enabled = enabled, modifier = modifier.heightIn(min = 48.dp), shape = RoundedCornerShape(6.dp),
        border = BorderStroke(1.dp, if (primary) Hud.Accent else Hud.BorderStrong),
        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 6.dp),
        colors = ButtonDefaults.buttonColors(containerColor = bg, contentColor = fg, disabledContainerColor = Hud.Panel2, disabledContentColor = Hud.Dim)) {
        Text(text, fontWeight = FontWeight.Bold, fontSize = 14.sp, maxLines = 2, softWrap = text.contains(' '), textAlign = androidx.compose.ui.text.style.TextAlign.Center)
    }
}

/** Segmented choice (mode bar, centre view, group by): whole segment is the target. */
@Composable
fun Segments(options: List<String>, selected: Int, modifier: Modifier = Modifier, enabled: (Int) -> Boolean = { true }, onSelect: (Int) -> Unit) {
    Row(modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        options.forEachIndexed { i, label ->
            val on = i == selected
            Box(Modifier.heightIn(min = 48.dp).background(if (on) Color(0xFF3A1D08) else Hud.Panel, RoundedCornerShape(6.dp))
                .border(1.dp, if (on) Hud.Accent else Hud.Border, RoundedCornerShape(6.dp))
                .clickable(enabled = enabled(i), role = Role.Tab) { onSelect(i) }.padding(horizontal = 14.dp)
                .semantics { contentDescription = label + if (on) ", selected" else "" }, contentAlignment = Alignment.Center) {
                Text(label, color = if (!enabled(i)) Hud.Dim else if (on) Hud.Accent else Hud.Text, fontWeight = if (on) FontWeight.Bold else FontWeight.Normal, fontSize = 14.sp)
            }
        }
    }
}

/** Filter chip with a 48 dp hit area. */
@Composable
fun HudChip(label: String, on: Boolean, onToggle: () -> Unit) {
    Box(Modifier.heightIn(min = 48.dp).clickable(role = Role.Checkbox) { onToggle() }.padding(vertical = 6.dp), contentAlignment = Alignment.Center) {
        Text(label, color = if (on) Color.Black else Hud.Text, fontSize = 13.sp,
            modifier = Modifier.background(if (on) Hud.Accent else Hud.Panel2, RoundedCornerShape(16.dp)).border(1.dp, if (on) Hud.Accent else Hud.Border, RoundedCornerShape(16.dp)).padding(horizontal = 12.dp, vertical = 8.dp))
    }
}

/** Status badge: colour plus a word (never colour alone). */
@Composable
fun Badge(text: String, color: Color, modifier: Modifier = Modifier) =
    Text(text, color = color, fontSize = 11.sp, fontWeight = FontWeight.Bold, modifier = modifier.border(1.dp, color, RoundedCornerShape(4.dp)).padding(horizontal = 6.dp, vertical = 2.dp))

@Composable
fun Field(label: String, value: String, modifier: Modifier = Modifier, number: Boolean = false, singleLine: Boolean = true, placeholder: String = "", onChange: (String) -> Unit) {
    OutlinedTextField(value, onChange, modifier = modifier.fillMaxWidth().padding(vertical = 3.dp), label = { Text(label) }, singleLine = singleLine,
        placeholder = { if (placeholder.isNotEmpty()) Text(placeholder, color = Hud.Dim) },
        keyboardOptions = if (number) KeyboardOptions(keyboardType = KeyboardType.Decimal) else KeyboardOptions.Default,
        colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = Hud.Accent, unfocusedBorderColor = Hud.Border, focusedLabelColor = Hud.Accent, unfocusedLabelColor = Hud.Muted, cursorColor = Hud.Accent))
}

@Composable
fun CheckRow(label: String, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable(enabled = enabled, role = Role.Checkbox) { onChange(!checked) }, verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked, null, enabled = enabled, colors = CheckboxDefaults.colors(checkedColor = Hud.Accent, checkmarkColor = Color.Black, uncheckedColor = Hud.Muted))
        Text(label, color = if (enabled) Hud.Text else Hud.Dim, fontSize = 14.sp, modifier = Modifier.padding(start = 8.dp))
    }
}

@Composable
fun SwitchRow(label: String, checked: Boolean, sub: String = "", onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).clickable(role = Role.Switch) { onChange(!checked) }, verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) { Text(label, color = Hud.Text, fontSize = 15.sp); if (sub.isNotEmpty()) Muted(sub, size = 12) }
        Switch(checked, null, colors = SwitchDefaults.colors(checkedThumbColor = Color.Black, checkedTrackColor = Hud.Accent))
    }
}

/** Exposed dropdown (48 dp). */
@Composable
fun Dropdown(label: String, options: List<String>, selected: Int, modifier: Modifier = Modifier, onSelect: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box(modifier) {
        Column(Modifier.fillMaxWidth().heightIn(min = 56.dp).border(1.dp, Hud.Border, RoundedCornerShape(4.dp)).clickable { open = true }.padding(horizontal = 12.dp, vertical = 6.dp)) {
            Muted(label, size = 11)
            Row(verticalAlignment = Alignment.CenterVertically) { Text(options.getOrElse(selected) { "" }, color = Hud.Text, fontSize = 15.sp, modifier = Modifier.weight(1f)); Text("▾", color = Hud.Accent) }
        }
        DropdownMenu(open, { open = false }) { options.forEachIndexed { i, o -> DropdownMenuItem({ Text(o) }, { onSelect(i); open = false }, modifier = Modifier.heightIn(min = 48.dp)) } }
    }
}

@Composable
fun KeyValue(k: String, v: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 3.dp)) {
        Text(k, color = Hud.Muted, fontSize = 13.sp, modifier = Modifier.width(170.dp))
        Text(v.ifBlank { "—" }, color = Hud.Text, fontSize = 13.sp, modifier = Modifier.weight(1f))
    }
}

/** Simple table with a header, used for results. Horizontally scrollable on narrow windows. */
@Composable
fun Table(headers: List<String>, rows: List<List<String>>, widths: List<Int>, maxRows: Int = 500) {
    Column(Modifier.horizontalScroll(rememberScrollState())) {
        Row(Modifier.background(Hud.Panel2)) { headers.forEachIndexed { i, h -> Text(h, color = Hud.Accent, fontSize = 12.sp, fontWeight = FontWeight.Bold, modifier = Modifier.width(widths[i].dp).padding(6.dp)) } }
        rows.take(maxRows).forEach { r ->
            Row(Modifier.border(BorderStroke(0.5.dp, Hud.Border))) { r.forEachIndexed { i, c -> Text(c, color = Hud.Text, fontSize = 12.sp, modifier = Modifier.width(widths[i].dp).padding(6.dp)) } }
        }
        if (rows.size > maxRows) Muted("${rows.size - maxRows} more rows in the exports.", Modifier.padding(6.dp))
    }
}

@Composable
fun Mono(text: String, color: Color = Hud.Text, size: Int = 12) = Text(text, color = color, fontSize = size.sp, fontFamily = FontFamily.Monospace, maxLines = 3, overflow = TextOverflow.Ellipsis)

@Composable
fun InfoDialog(title: String, text: String, onDismiss: () -> Unit) = AlertDialog(onDismiss, confirmButton = { TextButton(onDismiss) { Text("Close") } },
    title = { Text(title) }, text = { Text(text, color = Hud.Text) }, containerColor = Hud.Panel)

@Composable
fun ConfirmDialog(title: String, text: String, confirm: String, onConfirm: () -> Unit, onDismiss: () -> Unit) = AlertDialog(onDismiss,
    confirmButton = { TextButton({ onConfirm(); onDismiss() }) { Text(confirm, color = Hud.Accent) } }, dismissButton = { TextButton(onDismiss) { Text("Cancel") } },
    title = { Text(title) }, text = { Text(text, color = Hud.Text) }, containerColor = Hud.Panel)
