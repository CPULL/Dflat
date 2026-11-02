function testResult() {
	arrow = $("#arrow");
	arrowText = $("#arrowText");
	point = 0
	txt = txts[point];
	// move arrow to the new point
	// Type the text
	// wait the time
	moving = true;
	texting = false;
	waiting = false;
	time = 0;
	arrowText.html("");
	arrow.css("display", "block");
	$("#output").css("display", "none");
	if (interval) clearInterval(interval);
	interval = setInterval(animate, 15);
}

var time = 0;
var moving = true;
var texting = false;
var waiting = false;
var interval = null;
var arrow;
var arrowText;
function animate() {
	time++;
	if (moving) {
		var x = arrow.css("left");
		x = parseInt(x.slice(0, x.length - 2));
		var y = arrow.css("top");
		y = parseInt(y.slice(0, y.length - 2));

		x = (x + txt.x) / 2;
		y = (y + txt.y) / 2;
		arrow.css("left", x + "px");
		arrow.css("top", y + "px");

		if (Math.abs(x - txt.x) + Math.abs(y - txt.y) <= 2) {
			moving = false;
			texting = true;
		}
	}
	else if (texting) {
		if (time < 5) return;
		time = 0;
		arrowText.html(txt.m.slice(0, arrowText.html().length + 1));
		if (arrowText.html().length == txt.m.length) {
			texting = false;
			waiting = true;
			time = 0;
		}
	}
	else if (waiting) {
		if (time < txt.t) return;
			point++;
			if (point >= txts.length) {
				clearInterval(interval);
				interval = null;
				arrow.css("display", "none");
				$("#output").css("display", "block");
				return;
			}
			txt = txts[point];
			waiting = false;
			moving = true;
			arrowText.html("");
	}
}


var point = -1;
var txt;
const txts = [
	{x:150, y:225, t:150, m:"i32 specifies that the variable 'a' is a signed integer of 32 bits"},
	{x:104, y:249, t:150, m:"r64 specifies that the variable 'b' is a floating point variable of 64 bits (equivalent to a double)"},
	{x:294, y:297, t:150, m:"We convert the real number to an integer"},
	{x:298, y:297, t:150, m:"Converting directly will just truncate the decimals and transform 15.5 to 15"},
	{x:239, y:322, t:150, m:"Here we convert the integer to a floating point"},
	{x:402, y:345, t:150, m:"Here we do a complex calculation"},
	{x:198, y:393, t:150, m:"Here we print the result"},
	{x:552, y:392, t:150, m:"This part will be replaced with the value of 'allInteger'"},
	{x:1331, y:391, t:250, m:"Here we will put the value of 'complicated' with 5 decimals visible"},
	{x:1920, y:1080, t:0, m:""},
];


document.addEventListener('mousemove', (event) => {
  const x = event.clientX;
  const y = event.clientY;
  
  // Update the text inside the fixed div
  const coordsDiv = document.getElementById('cursor-coords');
  coordsDiv.textContent = `X: ${x} | Y: ${y}`;

//let arrow = $("#arrow");
//		arrow.css("left", x + "px");
//		arrow.css("top", y + "px");

});