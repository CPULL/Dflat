function testResult() {
	point = 0
	txt = txts[point];
	// move arrow to the new point
	// Type the text
	// wait the time
	moving = true;
	time = 0;
	setInterval(animate, 10);
}

var time = 0;

function animate() {
	time++;
	if (moving) {
		
	}

}


var point = -1;
var txt;
const txts = [
	{x:0, y:0, t:0, m:""},
	{x:0, y:0, t:0, m:""},
	{x:0, y:0, t:0, m:""},
	{x:0, y:0, t:0, m:""},
];